#pragma warning disable xUnit1051
using System.Text.Json;
using Cordia.Application.Extensions;
using Cordia.Application.Handlers.BookingFulfillment.Alternatives;
using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Extensions;
using Cordia.Infrastructure.Messaging;
using Cordia.Infrastructure.Repositories;
using Cordia.Infrastructure.Services;
using Cordia.Tests.IntegrationTests.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cordia.Tests.IntegrationTests.Messaging;

// ── Fake seams ───────────────────────────────────────────────────────────────────────────────

/// <summary>IValidationPolicy that always rejects with a fixed reason.</summary>
file sealed class CapstoneRejectPolicy(string reason) : IValidationPolicy
{
    public Task<string?> CheckAsync(string tenantId, string operationId, CancellationToken ct = default)
        => Task.FromResult<string?>(reason);
}

/// <summary>ICapacityReservation that always returns NoCapacity (for AlternativesRequired tests).</summary>
file sealed class CapstoneNoCapacityReservation : ICapacityReservation
{
    public Task<ReservationOutcome> ReserveAsync(ReserveRequest req, CancellationToken ct = default)
        => Task.FromResult(ReservationOutcome.NoCapacity());

    public Task ReleaseAsync(string tenantId, string holdId, ReleaseReason reason, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<int> ReleaseByOperationAsync(string tenantId, string operationId, ReleaseReason reason, CancellationToken ct = default)
        => Task.FromResult(0);

    public Task<FinalizeOutcome> FinalizeAgainstOperationAsync(string tenantId, string operationId, string workflowStep, CancellationToken ct = default)
        => Task.FromResult(FinalizeOutcome.NotTracked);
}

/// <summary>IDepositPolicy that always requires a deposit (used to drive op to AwaitingPayment).</summary>
file sealed class CapstoneDepositRequiredPolicy(int amount = 3000, string currency = "AUD") : IDepositPolicy
{
    public Task<DepositRequirement> GetRequirementAsync(string tenantId, string operationId, CancellationToken ct = default)
        => Task.FromResult(new DepositRequirement(true, amount, currency));
}

// ── Capstone state-machine + concurrency integration suite ────────────────────────────────────

/// <summary>
/// Capstone suite: §27.5 terminal-state reachability + §27.3 optimistic-concurrency races
/// (FM#4 two-workers-same-job, FM#5 stale-worker).
///
/// Each terminal test:
///   - Uses a unique per-test tenant_id (isolates Postgres rows).
///   - Neutralizes pre-existing unpublished outbox rows before driving the publisher.
///   - Drives through the real engine where the path is lightweight (≤ 2 publisher ticks).
///   - Asserts the terminal op status + the expected notification outbox row + audit event.
///
/// The OCC test uses BookingOperationRepository.TryTransitionStatusAsync directly with two
/// separate ApplicationDbContext instances (fixture.CreateContext) to prove:
///   FM#4: two workers contend on the same version → exactly one wins.
///   FM#5: a stale worker replaying expectedVersion=0 after a bump → 0 rows (rejected).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class StateMachineCapstoneTests(PostgresFixture fixture)
{
    // ── Container helpers ────────────────────────────────────────────────────────────────────

    private static ServiceProvider BuildContainer(
        IValidationPolicy? validationPolicy = null,
        ICapacityReservation? capacityReservation = null,
        IDepositPolicy? depositPolicy = null)
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

        // Override fakes AFTER AddInfrastructure to shadow registered services.
        if (validationPolicy is not null)
            services.AddSingleton(validationPolicy);
        if (capacityReservation is not null)
            services.AddScoped<ICapacityReservation>(_ => capacityReservation);
        if (depositPolicy is not null)
            services.AddSingleton(depositPolicy);

        return services.BuildServiceProvider();
    }

    private static OutboxPublisherHostedService BuildPublisher(IServiceProvider sp)
    {
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var options = sp.GetRequiredService<IOptions<MessagingOptions>>();
        return new OutboxPublisherHostedService(
            scopeFactory, options, NullLogger<OutboxPublisherHostedService>.Instance);
    }

    /// <summary>
    /// Neutralizes all unpublished outbox rows in the shared test DB so the publisher
    /// only processes rows seeded by this test.
    /// </summary>
    private async Task NeutralizeExistingUnpublishedAsync(string cleanupTenant = "capstone-cleanup")
    {
        await using var ctx = fixture.CreateContext(cleanupTenant);
        await ctx.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.PublishedAt == null)
            .ExecuteUpdateAsync(b => b
                .SetProperty(m => m.PublishedAt, DateTimeOffset.UtcNow)
                .SetProperty(m => m.LeaseOwner, (string?)null)
                .SetProperty(m => m.LeaseExpiresAt, (DateTimeOffset?)null));
    }

    /// <summary>Seeds op (Accepted) + booking.command.requested.v1 trigger outbox row. Returns operationId.</summary>
    private async Task<string> SeedTriggerAsync(string tenantId)
    {
        await using var seedCtx = fixture.CreateContext(tenantId);
        var operationId = await TestData.SeedOperationAsync(seedCtx, tenantId);
        seedCtx.OutboxMessages.Add(new OutboxMessage
        {
            TenantId = tenantId,
            MessageId = $"msg-{Guid.NewGuid():N}",
            MessageType = "booking.command.requested.v1",
            CorrelationId = operationId,
            OperationId = operationId,
            Payload = $"{{\"operation_id\":\"{operationId}\",\"tenant_id\":\"{tenantId}\"}}",
            OccurredAt = DateTimeOffset.UtcNow,
        });
        await seedCtx.SaveChangesAsync();
        return operationId;
    }

    /// <summary>
    /// Seeds a fully-booked capacity slot, an op (Accepted) whose intent revision canonical payload
    /// maps to that slot, and a trigger outbox row. The slot is full (total=1, held=1) so the real
    /// ICapacityReservation will return NoCapacity → AlternativesRequired. Returns operationId.
    /// </summary>
    private async Task<string> SeedTriggerWithFullSlotAsync(string tenantId)
    {
        var resourceId = $"res-{Guid.NewGuid():N}";
        var requestedAt = DateTimeOffset.UtcNow.AddHours(3);
        var slotId = $"{resourceId}|{requestedAt:O}";
        var canonicalPayload = JsonSerializer.Serialize(new
        {
            party_size = 1,
            resource_id = resourceId,
            starts_at = requestedAt.ToString("O"),
        });

        await using var seedCtx = fixture.CreateContext(tenantId);

        // Seed a FULL slot.
        seedCtx.CapacitySlots.Add(new CapacitySlot
        {
            TenantId = tenantId,
            SlotId = slotId,
            ResourceId = resourceId,
            StartsAt = requestedAt,
            TotalCapacity = 1,
            HeldCapacity = 1,
            BookedCapacity = 0,
            Version = 0,
        });

        // Seed intent + revision with canonical payload pointing to the full slot.
        var intentId = $"int-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var intent = new BookingIntent
        {
            TenantId = tenantId,
            IntentId = intentId,
            OperationType = OperationTypeEnum.Create,
        };
        var revision = new BookingIntentRevision
        {
            TenantId = tenantId,
            IntentId = intentId,
            Revision = 1,
            CanonicalPayload = canonicalPayload,
            PayloadHash = "h",
            SchemaVersion = "1.0",
            TenantPolicyVersion = "1.0",
            Intent = intent,
        };
        var operation = new BookingOperation
        {
            TenantId = tenantId,
            OperationId = operationId,
            IntentId = intentId,
            IntentRevision = 1,
            OperationType = OperationTypeEnum.Create,
            Status = OperationStatus.Accepted,
            Version = 0,
        };

        seedCtx.BookingIntents.Add(intent);
        seedCtx.BookingIntentRevisions.Add(revision);
        seedCtx.BookingOperations.Add(operation);

        seedCtx.OutboxMessages.Add(new OutboxMessage
        {
            TenantId = tenantId,
            MessageId = $"msg-{Guid.NewGuid():N}",
            MessageType = "booking.command.requested.v1",
            CorrelationId = operationId,
            OperationId = operationId,
            Payload = $"{{\"operation_id\":\"{operationId}\",\"tenant_id\":\"{tenantId}\"}}",
            OccurredAt = DateTimeOffset.UtcNow,
        });

        await seedCtx.SaveChangesAsync();
        return operationId;
    }

    // ── §27.5 Terminal reachability ──────────────────────────────────────────────────────────

    /// <summary>
    /// Booked terminal (§27.5): Accepted→…→Booked via the happy-path engine.
    /// Asserts notification.booking_confirmed.v1 outbox + operation.booked audit.
    /// </summary>
    [Fact]
    public async Task Terminal_Booked_ReachableViaEngine_EmitsExpectedEvents()
    {
        await NeutralizeExistingUnpublishedAsync();
        var tenantId = $"capstone-booked-{Guid.NewGuid():N}";
        var operationId = await SeedTriggerAsync(tenantId);

        // Default container: stub adapter creates booking, no deposit, always-valid policy.
        await using var sp = BuildContainer();
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        await using var ctx = fixture.CreateContext(tenantId);

        var op = await ctx.BookingOperations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.NotNull(op);
        Assert.Equal(OperationStatus.Booked, op!.Status);

        // notification.booking_confirmed.v1
        var notification = await ctx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId && m.OperationId == operationId &&
                m.MessageType == "notification.booking_confirmed.v1");
        Assert.NotNull(notification);

        // operation.booked audit event
        var audit = await ctx.AuditEvents.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e =>
                e.TenantId == tenantId && e.OperationId == operationId &&
                e.EventType == "operation.booked");
        Assert.NotNull(audit);
    }

    /// <summary>
    /// Rejected terminal (§27.5): Accepted→Validating→Rejected via fake IValidationPolicy.
    /// Asserts notification.rejected.v1 outbox + operation.rejected audit.
    /// </summary>
    [Fact]
    public async Task Terminal_Rejected_ReachableViaEngine_EmitsExpectedEvents()
    {
        await NeutralizeExistingUnpublishedAsync();
        var tenantId = $"capstone-rejected-{Guid.NewGuid():N}";
        var operationId = await SeedTriggerAsync(tenantId);

        await using var sp = BuildContainer(validationPolicy: new CapstoneRejectPolicy("underage_capstone"));
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        await using var ctx = fixture.CreateContext(tenantId);

        var op = await ctx.BookingOperations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.NotNull(op);
        Assert.Equal(OperationStatus.Rejected, op!.Status);
        Assert.Equal("underage_capstone", op.StatusReason);

        var notification = await ctx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId && m.OperationId == operationId &&
                m.MessageType == "notification.rejected.v1");
        Assert.NotNull(notification);

        var audit = await ctx.AuditEvents.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e =>
                e.TenantId == tenantId && e.OperationId == operationId &&
                e.EventType == "operation.rejected");
        Assert.NotNull(audit);
    }

    /// <summary>
    /// Waitlisted terminal (§27.5): engine drives op to AlternativesRequired because the slot is
    /// fully booked (total=1, held=1), then RespondToAlternativesCommand(Waitlist) completes it.
    /// Asserts notification.waitlisted.v1 outbox + operation.waitlisted audit.
    /// </summary>
    [Fact]
    public async Task Terminal_Waitlisted_ReachableViaEngine_EmitsExpectedEvents()
    {
        await NeutralizeExistingUnpublishedAsync();
        var tenantId = $"capstone-waitlisted-{Guid.NewGuid():N}";
        // Seed op with a canonical payload pointing to a full slot → real NoCapacity.
        var operationId = await SeedTriggerWithFullSlotAsync(tenantId);

        // Phase 1: Real ICapacityReservation returns NoCapacity → AlternativesRequired.
        await using (var sp = BuildContainer())
        {
            var publisher = BuildPublisher(sp);
            await publisher.PublishPendingBatchAsync(CancellationToken.None);
        }

        await using (var assertCtx = fixture.CreateContext(tenantId))
        {
            var mid = await assertCtx.BookingOperations.AsNoTracking()
                .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
            Assert.NotNull(mid);
            Assert.Equal(OperationStatus.AlternativesRequired, mid!.Status);
        }

        // Phase 2: Customer responds Waitlist via direct command.
        await using var sp2 = BuildContainer();
        using var scope = sp2.CreateScope();
        var tenantCtx = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenantCtx.TenantId = tenantId;
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Send(new RespondToAlternativesCommand(tenantId, operationId, AlternativesResponse.Waitlist));

        await using var ctx = fixture.CreateContext(tenantId);

        var op = await ctx.BookingOperations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.NotNull(op);
        Assert.Equal(OperationStatus.Waitlisted, op!.Status);

        var notification = await ctx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId && m.OperationId == operationId &&
                m.MessageType == "notification.waitlisted.v1");
        Assert.NotNull(notification);

        var audit = await ctx.AuditEvents.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e =>
                e.TenantId == tenantId && e.OperationId == operationId &&
                e.EventType == "operation.waitlisted");
        Assert.NotNull(audit);
    }

    /// <summary>
    /// FailedFinal terminal (§27.5): engine drives op to AlternativesRequired because the slot is
    /// fully booked (total=1, held=1), then RespondToAlternativesCommand(Decline) terminates it.
    /// Asserts notification.booking_failed.v1 outbox + operation.failed_final audit.
    /// </summary>
    [Fact]
    public async Task Terminal_FailedFinal_ReachableViaEngine_EmitsExpectedEvents()
    {
        await NeutralizeExistingUnpublishedAsync();
        var tenantId = $"capstone-failed-{Guid.NewGuid():N}";
        // Seed op with a canonical payload pointing to a full slot → real NoCapacity.
        var operationId = await SeedTriggerWithFullSlotAsync(tenantId);

        // Phase 1: Real ICapacityReservation returns NoCapacity → AlternativesRequired.
        await using (var sp = BuildContainer())
        {
            var publisher = BuildPublisher(sp);
            await publisher.PublishPendingBatchAsync(CancellationToken.None);
        }

        // Phase 2: Customer declines → FailedFinal.
        await using var sp2 = BuildContainer();
        using var scope = sp2.CreateScope();
        var tenantCtx = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenantCtx.TenantId = tenantId;
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Send(new RespondToAlternativesCommand(tenantId, operationId, AlternativesResponse.Decline));

        await using var ctx = fixture.CreateContext(tenantId);

        var op = await ctx.BookingOperations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.NotNull(op);
        Assert.Equal(OperationStatus.FailedFinal, op!.Status);

        var notification = await ctx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId && m.OperationId == operationId &&
                m.MessageType == "notification.booking_failed.v1");
        Assert.NotNull(notification);

        var audit = await ctx.AuditEvents.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e =>
                e.TenantId == tenantId && e.OperationId == operationId &&
                e.EventType == "operation.failed_final");
        Assert.NotNull(audit);
    }

    /// <summary>
    /// Expired terminal (§27.5): engine drives op to AwaitingPayment via FakeDepositPolicy,
    /// then a payment.deposit_expired.v1 event transitions it to Expired.
    /// Asserts notification.deposit_expired.v1 outbox + operation.expired audit.
    /// </summary>
    [Fact]
    public async Task Terminal_Expired_ReachableViaEngine_EmitsExpectedEvents()
    {
        await NeutralizeExistingUnpublishedAsync();
        var tenantId = $"capstone-expired-{Guid.NewGuid():N}";
        var operationId = await SeedTriggerAsync(tenantId);

        // Phase 1: Drive to AwaitingPayment (deposit required).
        await using (var sp = BuildContainer(depositPolicy: new CapstoneDepositRequiredPolicy()))
        {
            var publisher = BuildPublisher(sp);
            await publisher.PublishPendingBatchAsync(CancellationToken.None);
        }

        await using (var assertCtx = fixture.CreateContext(tenantId))
        {
            var mid = await assertCtx.BookingOperations.AsNoTracking()
                .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
            Assert.NotNull(mid);
            Assert.Equal(OperationStatus.AwaitingPayment, mid!.Status);
        }

        // Phase 2: Seed deposit_expired event + drive one publisher tick.
        await NeutralizeExistingUnpublishedAsync();
        await using (var evtCtx = fixture.CreateContext(tenantId))
        {
            evtCtx.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = $"evt-exp-{Guid.NewGuid():N}",
                MessageType = "payment.deposit_expired.v1",
                CorrelationId = operationId,
                OperationId = operationId,
                Payload = $"{{\"operation_id\":\"{operationId}\",\"tenant_id\":\"{tenantId}\"}}",
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await evtCtx.SaveChangesAsync();
        }

        await using (var sp = BuildContainer(depositPolicy: new CapstoneDepositRequiredPolicy()))
        {
            var publisher = BuildPublisher(sp);
            await publisher.PublishPendingBatchAsync(CancellationToken.None);
        }

        await using var ctx = fixture.CreateContext(tenantId);

        var op = await ctx.BookingOperations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.NotNull(op);
        Assert.Equal(OperationStatus.Expired, op!.Status);

        // notification.deposit_expired.v1 (emitted by the deposit handler on expiry)
        var notification = await ctx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId && m.OperationId == operationId &&
                m.MessageType == "notification.deposit_expired.v1");
        Assert.NotNull(notification);

        // operation.expired audit event
        var audit = await ctx.AuditEvents.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(e =>
                e.TenantId == tenantId && e.OperationId == operationId &&
                e.EventType == "operation.expired");
        Assert.NotNull(audit);
    }

    // ── §27.3 Optimistic-concurrency races (FM#4 + FM#5) ────────────────────────────────────

    /// <summary>
    /// Two-workers-same-job (FM#4) + stale-worker (FM#5) — §22.1 OCC proof.
    ///
    /// Protocol:
    ///   1. Seed op in Accepted (Version=0).
    ///   2. Context A calls TryTransitionStatusAsync(expectedVersion:0, Accepted→Validating) → 1 row (wins).
    ///   3. Context B calls the SAME transition with the SAME expectedVersion:0 → 0 rows (lost race).
    ///   4. Reload: op.Status==Validating, op.Version==1.
    ///   5. Stale replay: Context C calls TryTransitionStatusAsync(expectedVersion:0) after the bump → 0 rows.
    ///
    /// This proves:
    ///   FM#4: exactly one of two concurrent workers wins the CAS; the other is rejected.
    ///   FM#5: a stale-version replay is always rejected, even on the same expected-status.
    /// </summary>
    [Fact]
    public async Task OCC_TwoWorkersSameJob_ExactlyOneWins_FM4_And_FM5_StaleVersionRejected()
    {
        var tenantId = $"capstone-occ-{Guid.NewGuid():N}";

        // Seed op in Accepted, Version=0.
        string operationId;
        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Accepted);
        }

        // Confirm initial version.
        await using (var checkCtx = fixture.CreateContext(tenantId))
        {
            var seeded = await checkCtx.BookingOperations.AsNoTracking()
                .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
            Assert.NotNull(seeded);
            Assert.Equal(0L, seeded!.Version);
            Assert.Equal(OperationStatus.Accepted, seeded.Status);
        }

        // ── FM#4: two workers race on version=0 ──────────────────────────────────────────────

        // Context A: first worker — should win (1 row updated).
        await using var ctxA = fixture.CreateContext(tenantId);
        var repoA = new BookingOperationRepository(ctxA);
        var rowsA = await repoA.TryTransitionStatusAsync(
            tenantId, operationId,
            expectedVersion: 0,
            expectedStatus: OperationStatus.Accepted,
            next: OperationStatus.Validating,
            reason: "validating");

        // Context B: second worker racing the same version=0 — should lose (0 rows).
        await using var ctxB = fixture.CreateContext(tenantId);
        var repoB = new BookingOperationRepository(ctxB);
        var rowsB = await repoB.TryTransitionStatusAsync(
            tenantId, operationId,
            expectedVersion: 0,
            expectedStatus: OperationStatus.Accepted,
            next: OperationStatus.Validating,
            reason: "validating");

        // Exactly one winner.
        Assert.Equal(1, rowsA); // A won
        Assert.Equal(0, rowsB); // B lost — row was already bumped by A

        // ── Verify post-race state ───────────────────────────────────────────────────────────

        await using var reloadCtx = fixture.CreateContext(tenantId);
        var reloaded = await reloadCtx.BookingOperations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(reloaded);
        Assert.Equal(OperationStatus.Validating, reloaded!.Status);
        Assert.Equal(1L, reloaded.Version); // version bumped exactly once

        // ── FM#5: stale-version replay after the bump ────────────────────────────────────────

        // Context C: stale worker replays expectedVersion=0 after version is now 1 → 0 rows.
        await using var ctxC = fixture.CreateContext(tenantId);
        var repoC = new BookingOperationRepository(ctxC);
        var rowsStale = await repoC.TryTransitionStatusAsync(
            tenantId, operationId,
            expectedVersion: 0,           // stale — actual version is 1
            expectedStatus: OperationStatus.Accepted,
            next: OperationStatus.Validating,
            reason: "validating");

        Assert.Equal(0, rowsStale); // stale replay rejected — version guard holds

        // Op unchanged after stale attempt.
        await using var finalCtx = fixture.CreateContext(tenantId);
        var final = await finalCtx.BookingOperations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(final);
        Assert.Equal(OperationStatus.Validating, final!.Status);
        Assert.Equal(1L, final.Version); // unchanged after stale attempt
    }
}
