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

namespace Cordia.Tests.IntegrationTests.Messaging;

// ── Fake poison adapter ───────────────────────────────────────────────────────────────────────

/// <summary>
/// IBookingProviderAdapter that always throws <see cref="Domain.Exceptions.PoisonMessageException"/>
/// on CreateBookingAsync, simulating an invariant-violation defect for T11a Inv-14 tests.
/// </summary>
file sealed class PoisonThrowingAdapter : IBookingProviderAdapter
{
    public string ProviderName => "fake-poison";

    public Task<ProviderResult> CreateBookingAsync(ProviderBookingRequest request, CancellationToken ct = default)
        => throw new Domain.Exceptions.PoisonMessageException("Simulated poison: invariant violation in adapter.");

    public Task<ProviderResult> LookupByIdempotencyKeyAsync(string account, string key, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));

    public Task<ProviderResult> LookupByReferenceAsync(string account, string reference, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));

    public Task<ProviderResult> GetStatusAsync(string account, string reference, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));

    public Task<ProviderResult> CancelBookingAsync(string account, string reference, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));
}

// ── Tests ─────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Integration tests for Inv-14 / FM#14: poison/DLQ persist-before-dead-letter.
///
/// Spec contract (Inv-14): before dead-lettering the transport message, the consumer MUST persist
/// the recoverable/terminal business state + ops work-item + user comms in a COMMITTED transaction.
/// The operation stays in its current recoverable in-progress status (DLQ ≠ business state).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class PoisonDeadLetterTests(PostgresFixture fixture)
{
    private static ServiceProvider BuildContainer(IBookingProviderAdapter? fakeAdapter = null)
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

        if (fakeAdapter is not null)
            services.AddSingleton<IBookingProviderAdapter>(fakeAdapter);

        return services.BuildServiceProvider();
    }

    private static OutboxPublisherHostedService BuildPublisher(IServiceProvider sp)
    {
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var options = sp.GetRequiredService<IOptions<MessagingOptions>>();
        return new OutboxPublisherHostedService(
            scopeFactory, options, NullLogger<OutboxPublisherHostedService>.Instance);
    }

    private async Task NeutralizeExistingUnpublishedAsync(string cleanupTenant = "t11-poison-cleanup")
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

    /// <summary>
    /// FM#14 / Inv-14: adapter throws PoisonMessageException → driver persists work-item +
    /// notification.still_confirming.v1 + audit in a committed txn, THEN dead-letters the
    /// transport message. Op remains in its current in-progress status (not a DLQ status).
    /// The trigger outbox row is marked published (no infinite redelivery).
    /// </summary>
    [Fact]
    public async Task PoisonException_Inv14_PersistsBeforeDeadLetter_OpStaysInProgress()
    {
        await NeutralizeExistingUnpublishedAsync();

        var tenantId = $"t11-poison-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        string operationId;

        // Seed op in Accepted + trigger outbox row.
        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Accepted);

            seedCtx.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = messageId,
                MessageType = "booking.command.requested.v1",
                CorrelationId = operationId,
                OperationId = operationId,
                Payload = $"{{\"operation_id\":\"{operationId}\",\"tenant_id\":\"{tenantId}\"}}",
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await seedCtx.SaveChangesAsync();
        }

        await using var sp = BuildContainer(fakeAdapter: new PoisonThrowingAdapter());
        var publisher = BuildPublisher(sp);

        // One publisher tick: Accepted→Validating→ReservingCapacity→Finalizing→PoisonException.
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);

        // ── Assert: op is NOT in any terminal or DLQ status (Inv-14: DLQ ≠ business state) ───
        var op = await assertCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(op);
        // The op should be in a non-terminal in-progress state (e.g. Finalizing after the
        // validate+reserve steps ran, before the finalize step threw).
        Assert.False(OperationStateMachine.IsTerminal(op!.Status),
            $"Op should remain in a recoverable in-progress status; got {op.Status}");

        // ── Assert: dead_letter_messages row exists for this message_id ─────────────────────
        var dlq = await assertCtx.DeadLetterMessages
            .AsNoTracking()
            .FirstOrDefaultAsync(d =>
                d.TenantId == tenantId &&
                d.MessageId == messageId &&
                d.ConsumerName == "booking.driver");

        Assert.NotNull(dlq);
        Assert.Equal(DlqReplayState.Quarantined, dlq!.ReplayState);
        Assert.Equal("poison", dlq.ErrorClass);

        // ── Assert: open operations_work_item exists ─────────────────────────────────────────
        var workItem = await assertCtx.OperationWorkItems
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.TenantId == tenantId && w.OperationId == operationId);

        Assert.NotNull(workItem);
        Assert.Equal("open", workItem!.State);
        Assert.StartsWith("poison:", workItem.Reason);

        // ── Assert: notification.still_confirming.v1 outbox row exists ──────────────────────
        var notification = await assertCtx.OutboxMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId &&
                m.OperationId == operationId &&
                m.MessageType == "notification.still_confirming.v1");

        Assert.NotNull(notification);

        // ── Assert: operation.poison_quarantined audit event exists ─────────────────────────
        var audit = await assertCtx.AuditEvents
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e =>
                e.TenantId == tenantId &&
                e.OperationId == operationId &&
                e.EventType == "operation.poison_quarantined");

        Assert.NotNull(audit);

        // ── Assert: trigger outbox row marked published (no infinite redelivery) ─────────────
        var triggerRow = await assertCtx.OutboxMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.MessageId == messageId);

        Assert.NotNull(triggerRow);
        Assert.NotNull(triggerRow!.PublishedAt);
    }

    /// <summary>
    /// Idempotency: running the poison path a second time (via the deterministic outbox message_id)
    /// does NOT create a duplicate notification.still_confirming.v1 row.
    /// </summary>
    [Fact]
    public async Task PoisonException_DeterministicOutboxId_Deduplicates()
    {
        await NeutralizeExistingUnpublishedAsync();

        var tenantId = $"t11-poison-dedup-{Guid.NewGuid():N}";
        string operationId;

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Accepted);

            // Two trigger outbox rows with different message_ids.
            for (var i = 0; i < 2; i++)
            {
                seedCtx.OutboxMessages.Add(new OutboxMessage
                {
                    TenantId = tenantId,
                    MessageId = $"msg-dedup-{i}-{Guid.NewGuid():N}",
                    MessageType = "booking.command.requested.v1",
                    CorrelationId = operationId,
                    OperationId = operationId,
                    Payload = $"{{\"operation_id\":\"{operationId}\",\"tenant_id\":\"{tenantId}\"}}",
                    OccurredAt = DateTimeOffset.UtcNow,
                });
            }
            await seedCtx.SaveChangesAsync();
        }

        await using var sp = BuildContainer(fakeAdapter: new PoisonThrowingAdapter());
        var publisher = BuildPublisher(sp);

        // Two publisher ticks simulating redelivery.
        await publisher.PublishPendingBatchAsync(CancellationToken.None);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);

        // notification.still_confirming.v1 must appear exactly once (deterministic dedup).
        var count = await assertCtx.OutboxMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .CountAsync(m =>
                m.TenantId == tenantId &&
                m.OperationId == operationId &&
                m.MessageType == "notification.still_confirming.v1");

        Assert.Equal(1, count);
    }
}
