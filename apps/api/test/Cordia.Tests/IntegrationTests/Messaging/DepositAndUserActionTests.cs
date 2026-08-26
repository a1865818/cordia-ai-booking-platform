#pragma warning disable xUnit1051
using Cordia.Application.Extensions;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Extensions;
using Cordia.Infrastructure.Messaging;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Cordia.Tests.IntegrationTests.Messaging;

// ── Fake seam implementations ────────────────────────────────────────────────────────────────

file sealed class FakeDepositPolicy(bool required, int amount = 5000, string currency = "AUD") : IDepositPolicy
{
    public Task<DepositRequirement> GetRequirementAsync(string tenantId, string operationId, CancellationToken ct = default)
        => Task.FromResult(required ? new DepositRequirement(true, amount, currency) : DepositRequirement.None);
}

file sealed class FakeVerificationService(bool required) : IVerificationService
{
    public Task<bool> RequiresUserActionAsync(string tenantId, string operationId, CancellationToken ct = default)
        => Task.FromResult(required);
}

// ── Tests ────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// End-to-end integration tests for T7 (deposit + user-action paths), spec 06 §6.1 / §10.
///
/// Pattern mirrors FinalizeHappyPathTests: full DI container, real Postgres, drive via
/// OutboxPublisherHostedService.PublishPendingBatchAsync, assert DB state.
///
/// Override IDepositPolicy / IVerificationService AFTER AddInfrastructure to replace
/// the default singletons with fakes for the tests that need them.
///
/// All events are delivered via seeded outbox rows so the publisher sets TenantContext
/// (via PostgresMessageTransport) before dispatching — the global query filter sees the
/// correct tenant_id.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class DepositAndUserActionTests(PostgresFixture fixture)
{
    // ── Container helpers ────────────────────────────────────────────────────────────────────

    private static ServiceProvider BuildContainer(
        string tenantId,
        IDepositPolicy? depositPolicy = null,
        IVerificationService? verificationService = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CordiaDBConnection"] = PostgresFixture.TestConnStr,
                ["Messaging:Publisher:Enabled"] = "false",
                ["Messaging:ScheduledJobPoller:Enabled"] = "false",
                ["Messaging:StepSweeper:Enabled"] = "false",
                ["Messaging:NotificationReconcile:Enabled"] = "false",
                ["Messaging:MissingNotificationAudit:Enabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddApplication();
        services.AddInfrastructure(config);
        services.ScopeOutboxClaimToTenant(tenantId);

        // Override fakes AFTER AddInfrastructure so they shadow the default singletons.
        if (depositPolicy is not null)
            services.AddSingleton(depositPolicy);
        if (verificationService is not null)
            services.AddSingleton(verificationService);

        return services.BuildServiceProvider();
    }

    private static OutboxPublisherHostedService BuildPublisher(IServiceProvider sp)
    {
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var options = sp.GetRequiredService<IOptions<MessagingOptions>>();
        return new OutboxPublisherHostedService(
            scopeFactory, options,
            NullLogger<OutboxPublisherHostedService>.Instance);
    }

    // There used to be a NeutralizeExistingUnpublishedAsync() here that stamped EVERY tenant's
    // unpublished outbox rows as published before each tick, so the cross-tenant claim would only
    // find this test's message. It could not work: it settles the backlog that exists at that
    // instant, and the AcceptanceApi collection — which xUnit runs in parallel against the same
    // database — keeps inserting booking.command.requested.v1 rows afterwards. It also stamped
    // those foreign rows published, corrupting a concurrent test's state. Bounding the claim to
    // this test's tenant (OutboxTenantScope) is what actually isolates the tick, and it makes the
    // sweep unnecessary: every test here mints a fresh GUID tenant with no backlog of its own.

    /// <summary>Seeds an op in Accepted status + booking.command.requested.v1 trigger outbox row.</summary>
    private async Task<(string tenantId, string operationId)> SeedOperationAndTriggerAsync(string prefix)
    {
        var tenantId = $"{prefix}-{Guid.NewGuid():N}";

        await using var seedCtx = fixture.CreateContext(tenantId);
        var operationId = await TestData.SeedOperationAsync(seedCtx, tenantId);

        seedCtx.OutboxMessages.Add(new OutboxMessage
        {
            TenantId = tenantId,
            MessageId = $"msg-{Guid.NewGuid():N}",
            MessageType = "booking.command.requested.v1",
            CorrelationId = operationId,
            OperationId = operationId,
            Payload = JsonSerializer.Serialize(new { operation_id = operationId, tenant_id = tenantId }),
            OccurredAt = DateTimeOffset.UtcNow,
        });
        await seedCtx.SaveChangesAsync();

        return (tenantId, operationId);
    }

    /// <summary>Seeds a payment event outbox row to simulate the deposit provider callback.
    /// Returns the message_id used (caller may pass a fixed one to simulate a duplicate delivery).</summary>
    private async Task<string> SeedPaymentEventAsync(
        string tenantId, string operationId, string eventType, string? messageId = null)
    {
        var msgId = messageId ?? $"evt-{Guid.NewGuid():N}";
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.OutboxMessages.Add(new OutboxMessage
        {
            TenantId = tenantId,
            MessageId = msgId,
            MessageType = eventType,
            CorrelationId = operationId,
            OperationId = operationId,
            Payload = JsonSerializer.Serialize(new { operation_id = operationId, tenant_id = tenantId }),
            OccurredAt = DateTimeOffset.UtcNow,
        });
        await ctx.SaveChangesAsync();
        return msgId;
    }

    /// <summary>Resets a single outbox row to unpublished so the publisher re-delivers the SAME
    /// message_id (simulates a crash-before-mark redelivery, §5.2).</summary>
    private async Task RepublishOutboxRowAsync(string tenantId, string messageId)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        await ctx.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && m.MessageId == messageId)
            .ExecuteUpdateAsync(b => b
                .SetProperty(m => m.PublishedAt, (DateTimeOffset?)null)
                .SetProperty(m => m.LeaseOwner, (string?)null)
                .SetProperty(m => m.LeaseExpiresAt, (DateTimeOffset?)null));
    }

    /// <summary>Seeds a user action completed outbox row.</summary>
    private async Task SeedUserActionEventAsync(string tenantId, string operationId)
        => await SeedPaymentEventAsync(tenantId, operationId, "booking.user_action.completed.v1");

    // ── Test 1: No-deposit regression ────────────────────────────────────────────────────────

    [Fact]
    public async Task NoDeposit_DefaultPolicy_OperationReachesBooked()
    {
        // Confirms the RequestPayment no-deposit path: Held→Finalizing→Booked (one publisher tick).
        var (tenantId, operationId) = await SeedOperationAndTriggerAsync("t7-reg");

        await using var sp = BuildContainer(tenantId); // default policies (no deposit, no user action)
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        await using var ctx = fixture.CreateContext(tenantId);
        var op = await ctx.BookingOperations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(op);
        Assert.Equal(OperationStatus.Booked, op!.Status);
    }

    // ── Test 2: Deposit happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Deposit_HappyPath_AwaitingPaymentThenBooked()
    {
        var (tenantId, operationId) = await SeedOperationAndTriggerAsync("t7-dep");

        // Phase A: drive to AwaitingPayment (deposit required fake).
        await using (var sp = BuildContainer(tenantId, depositPolicy: new FakeDepositPolicy(required: true)))
        {
            var publisher = BuildPublisher(sp);
            await publisher.PublishPendingBatchAsync(CancellationToken.None);
        }

        // Assert Phase A.
        await using (var assertCtx = fixture.CreateContext(tenantId))
        {
            var op = await assertCtx.BookingOperations.AsNoTracking()
                .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
            Assert.NotNull(op);
            Assert.Equal(OperationStatus.AwaitingPayment, op!.Status);

            var session = await assertCtx.DepositSessions.AsNoTracking()
                .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.OperationId == operationId);
            Assert.NotNull(session);
            Assert.Equal(DepositStatus.Created, session!.Status);
            Assert.Equal(5000, session.AmountMinor);
            Assert.Equal("AUD", session.Currency);

            var linkOutbox = await assertCtx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
                .FirstOrDefaultAsync(m =>
                    m.TenantId == tenantId && m.OperationId == operationId &&
                    m.MessageType == "payment.deposit_link.requested.v1");
            Assert.NotNull(linkOutbox);
        }

        // Phase B: simulate deposit_paid event via outbox row (publisher sets tenant context).
        await SeedPaymentEventAsync(tenantId, operationId, "payment.deposit_paid.v1");

        await using (var sp = BuildContainer(tenantId, depositPolicy: new FakeDepositPolicy(required: true)))
        {
            var publisher = BuildPublisher(sp);
            // First tick: deposit_paid → Finalizing + continuation booking.command.requested.v1 emitted.
            await publisher.PublishPendingBatchAsync(CancellationToken.None);
            // Second tick: continuation booking.command.requested.v1 → driver → Booked.
            await publisher.PublishPendingBatchAsync(CancellationToken.None);
        }

        // Assert Phase B: op=Booked, DepositSession=Paid.
        await using var finalCtx = fixture.CreateContext(tenantId);

        var finalOp = await finalCtx.BookingOperations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.NotNull(finalOp);
        Assert.Equal(OperationStatus.Booked, finalOp!.Status);

        var finalSession = await finalCtx.DepositSessions.AsNoTracking()
            .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.OperationId == operationId);
        Assert.NotNull(finalSession);
        Assert.Equal(DepositStatus.Paid, finalSession!.Status);
        Assert.NotNull(finalSession.PaidAt);
    }

    // ── Test 3: Deposit expired ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Deposit_Expired_OperationExpiredAndOutboxesEmitted()
    {
        var (tenantId, operationId) = await SeedOperationAndTriggerAsync("t7-exp");

        // Drive to AwaitingPayment.
        await using (var sp = BuildContainer(tenantId, depositPolicy: new FakeDepositPolicy(required: true)))
        {
            var publisher = BuildPublisher(sp);
            await publisher.PublishPendingBatchAsync(CancellationToken.None);
        }

        // Simulate deposit_expired event.
        await SeedPaymentEventAsync(tenantId, operationId, "payment.deposit_expired.v1");

        await using (var sp = BuildContainer(tenantId, depositPolicy: new FakeDepositPolicy(required: true)))
        {
            var publisher = BuildPublisher(sp);
            await publisher.PublishPendingBatchAsync(CancellationToken.None);
        }

        // Assert: op=Expired, DepositSession=Expired, hold_release + deposit_expired outbox rows.
        await using var ctx = fixture.CreateContext(tenantId);

        var op = await ctx.BookingOperations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.NotNull(op);
        Assert.Equal(OperationStatus.Expired, op!.Status);

        var session = await ctx.DepositSessions.AsNoTracking()
            .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.OperationId == operationId);
        Assert.NotNull(session);
        Assert.Equal(DepositStatus.Expired, session!.Status);

        var holdRelease = await ctx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId && m.OperationId == operationId &&
                m.MessageType == "capacity.hold_release.requested.v1");
        Assert.NotNull(holdRelease);

        var depositExpiredNotif = await ctx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId && m.OperationId == operationId &&
                m.MessageType == "notification.deposit_expired.v1");
        Assert.NotNull(depositExpiredNotif);
    }

    // ── Test 4: User-action path ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task UserAction_RequiredThenCompleted_OperationReachesBooked()
    {
        var (tenantId, operationId) = await SeedOperationAndTriggerAsync("t7-ua");

        // Phase A: drive to AwaitingUserAction (verification required).
        await using (var sp = BuildContainer(tenantId, verificationService: new FakeVerificationService(required: true)))
        {
            var publisher = BuildPublisher(sp);
            await publisher.PublishPendingBatchAsync(CancellationToken.None);
        }

        await using (var assertCtx = fixture.CreateContext(tenantId))
        {
            var op = await assertCtx.BookingOperations.AsNoTracking()
                .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
            Assert.NotNull(op);
            Assert.Equal(OperationStatus.AwaitingUserAction, op!.Status);

            var notifOutbox = await assertCtx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
                .FirstOrDefaultAsync(m =>
                    m.TenantId == tenantId && m.OperationId == operationId &&
                    m.MessageType == "notification.user_action_required.v1");
            Assert.NotNull(notifOutbox);
        }

        // Phase B: simulate user action completed. Verification now returns false → re-validation passes.
        await SeedUserActionEventAsync(tenantId, operationId);

        await using (var sp = BuildContainer(tenantId, verificationService: new FakeVerificationService(required: false)))
        {
            var publisher = BuildPublisher(sp);
            // First tick: user_action.completed → Accepted + continuation booking.command.requested.v1 emitted.
            await publisher.PublishPendingBatchAsync(CancellationToken.None);
            // Second tick: driver → Validating → ReservingCapacity → Held → RequestPayment → Finalizing → Booked.
            await publisher.PublishPendingBatchAsync(CancellationToken.None);
        }

        // Assert: op=Booked.
        await using var finalCtx = fixture.CreateContext(tenantId);
        var finalOp = await finalCtx.BookingOperations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.NotNull(finalOp);
        Assert.Equal(OperationStatus.Booked, finalOp!.Status);
    }

    // ── Test 5: Dedup — second deposit_paid delivery after state advanced is idempotent ────────

    [Fact]
    public async Task Deposit_DuplicatePaidDelivery_SameMessageId_InboxSuppressesSecond()
    {
        // Proves Inv-5 inbox dedup for the deposit consumer (booking.deposit): re-delivering the
        // SAME payment.deposit_paid.v1 message_id must admit exactly one consumer_inbox row and
        // transition the operation exactly once (the InboxDeduplicationBehavior DO-NOTHING path
        // suppresses the second delivery before the handler runs).
        var (tenantId, operationId) = await SeedOperationAndTriggerAsync("t7-dup");

        // Phase A: drive to AwaitingPayment.
        await using (var sp = BuildContainer(tenantId, depositPolicy: new FakeDepositPolicy(required: true)))
        {
            var publisher = BuildPublisher(sp);
            await publisher.PublishPendingBatchAsync(CancellationToken.None);
        }

        // Phase B: first paid event (FIXED message_id) → op AwaitingPayment→Finalizing, inbox admits.
        var paidMessageId = $"evt-dup-{Guid.NewGuid():N}";
        await SeedPaymentEventAsync(tenantId, operationId, "payment.deposit_paid.v1", paidMessageId);

        await using (var sp = BuildContainer(tenantId, depositPolicy: new FakeDepositPolicy(required: true)))
        {
            var publisher = BuildPublisher(sp);
            await publisher.PublishPendingBatchAsync(CancellationToken.None);
        }

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var op = await ctx.BookingOperations.AsNoTracking()
                .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
            Assert.NotNull(op);
            Assert.Equal(OperationStatus.Finalizing, op!.Status);
        }

        // Phase C: REDELIVER the exact same message_id (reset published_at on the same outbox row)
        // and neutralize the Phase-B continuation so only the duplicate is in the batch.
        await RepublishOutboxRowAsync(tenantId, paidMessageId);
        await using (var neutralCtx = fixture.CreateContext("t7-dup-neutralize"))
        {
            await neutralCtx.OutboxMessages
                .IgnoreQueryFilters()
                .Where(m => m.PublishedAt == null && m.MessageId != paidMessageId)
                .ExecuteUpdateAsync(b => b
                    .SetProperty(m => m.PublishedAt, DateTimeOffset.UtcNow)
                    .SetProperty(m => m.LeaseOwner, (string?)null)
                    .SetProperty(m => m.LeaseExpiresAt, (DateTimeOffset?)null));
        }

        await using (var sp = BuildContainer(tenantId, depositPolicy: new FakeDepositPolicy(required: true)))
        {
            var publisher = BuildPublisher(sp);
            await publisher.PublishPendingBatchAsync(CancellationToken.None);
        }

        await using var finalCtx = fixture.CreateContext(tenantId);
        var finalOp = await finalCtx.BookingOperations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.NotNull(finalOp);
        Assert.Equal(OperationStatus.Finalizing, finalOp!.Status);

        // EXACTLY one inbox row for (booking.deposit, tenant, paidMessageId) — the duplicate was
        // suppressed by the InboxDeduplicationBehavior before the handler ran.
        var inboxCount = await finalCtx.ConsumerInbox.AsNoTracking().IgnoreQueryFilters()
            .CountAsync(i =>
                i.TenantId == tenantId &&
                i.ConsumerName == "booking.deposit" &&
                i.MessageId == paidMessageId);
        Assert.Equal(1, inboxCount);
    }
}
