#pragma warning disable xUnit1051
using Cordia.Application.Extensions;
using Cordia.Application.Messaging;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Extensions;
using Cordia.Infrastructure.Messaging;
using Cordia.Tests.IntegrationTests.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cordia.Tests.IntegrationTests.Messaging;

// ── In-test consumer types ───────────────────────────────────────────────────────────────────

/// <summary>
/// Queue-driven request wired for the E2E capstone test.  The ConsumerName is "e2e.capstone.consumer";
/// OperationId carries the FK needed by the handler's WorkflowStep write.
/// </summary>
file sealed record E2EConsumerRequest(string TenantId, string MessageId, string OperationId)
    : IRequest<Unit>, IInboxConsumerRequest
{
    public string ConsumerName => "e2e.capstone.consumer";
    string? IInboxConsumerRequest.CorrelationId => OperationId;
    string? IInboxConsumerRequest.OperationId => OperationId;
}

/// <summary>
/// Consumer handler for the E2E test.  Writes a durable WorkflowStep row inside the ambient
/// inbox transaction (Inv-5, §5.1) to prove the handler ran.
/// </summary>
file sealed class E2EConsumerHandler(ApplicationDbContext db) : IRequestHandler<E2EConsumerRequest, Unit>
{
    public async Task<Unit> Handle(E2EConsumerRequest request, CancellationToken cancellationToken)
    {
        db.WorkflowSteps.Add(new WorkflowStep
        {
            TenantId = request.TenantId,
            WorkflowStepId = $"e2e-step-{request.MessageId}",
            OperationId = request.OperationId,
            StepType = "e2e.capstone.step",
            Status = WorkflowStepStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}

// ── Test class ───────────────────────────────────────────────────────────────────────────────

/// <summary>
/// End-to-end capstone integration test (T8, spec 05 §5.1/§5.2/§11).
///
/// Builds a full DI container (AddApplication + AddInfrastructure) and drives the complete
/// durable-messaging chain:
///   outbox row → OutboxPublisherHostedService.PublishPendingBatchAsync
///              → PostgresMessageTransport → MediatRQueueConsumerDispatcher
///              → InboxDeduplicationBehavior → E2EConsumerHandler (writes WorkflowStep).
///
/// Test A proves the happy-path (§5.1): outbox row admitted → consumer ran → step written.
/// Test B proves duplicate suppression (§5.2/§11): second delivery of same message_id
///   → inbox row count = 1, step count = 1 (handler NOT called again).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class EndToEndSubstrateTests(PostgresFixture fixture)
{
    // ── Container builder ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the full production DI graph (Application + Infrastructure) pointed at the test DB.
    /// IConfiguration is also registered in the container so that AddOptions().BindConfiguration()
    /// (used by MessagingOptions) can resolve it at runtime.
    /// The test consumer handler is explicitly registered because it lives in the test assembly
    /// and is not picked up by AddApplication's assembly scan.
    /// Hosted services are never started (IHostedService.StartAsync is not called by a manually
    /// constructed ServiceProvider); the publisher is driven via PublishPendingBatchAsync directly.
    /// </summary>
    private static ServiceProvider BuildContainer(string tenantId)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CordiaDBConnection"] = PostgresFixture.TestConnStr,
                // Belt-and-suspenders: disable hosted services even though a manually built
                // ServiceProvider never calls StartAsync.
                ["Messaging:Publisher:Enabled"] = "false",
                ["Messaging:ScheduledJobPoller:Enabled"] = "false",
                ["Messaging:StepSweeper:Enabled"] = "false",
                ["Messaging:NotificationReconcile:Enabled"] = "false",
                ["Messaging:MissingNotificationAudit:Enabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        // IConfiguration must be registered so that AddOptions().BindConfiguration(section)
        // can resolve it when building IOptions<MessagingOptions>.
        services.AddSingleton<IConfiguration>(config);
        services.AddApplication();
        services.AddInfrastructure(config);
        services.ScopeOutboxClaimToTenant(tenantId);

        // Register the test consumer handler; it lives in the test assembly so AddApplication's
        // MediatR assembly-scan does not discover it.
        services.AddTransient<IRequestHandler<E2EConsumerRequest, Unit>, E2EConsumerHandler>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Constructs an OutboxPublisherHostedService from the container's IServiceScopeFactory
    /// and MessagingOptions.  AddMessaging registers the service via AddHostedService (as
    /// IHostedService only), so GetRequiredService&lt;OutboxPublisherHostedService&gt; would fail;
    /// we instantiate the concrete type directly, mirroring OutboxPublisherTests.BuildService.
    /// </summary>
    private static OutboxPublisherHostedService BuildPublisher(IServiceProvider sp)
    {
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var options = sp.GetRequiredService<IOptions<MessagingOptions>>();
        return new OutboxPublisherHostedService(
            scopeFactory,
            options,
            NullLogger<OutboxPublisherHostedService>.Instance);
    }

    /// <summary>
    /// Marks all pre-existing unpublished outbox rows as published so the publisher batch
    /// reaches only the freshly seeded row.  Mirrors OutboxPublisherTests.NeutralizeExistingUnpublishedAsync.
    /// </summary>
    // There used to be a NeutralizeExistingUnpublishedAsync() here that stamped EVERY tenant's
    // unpublished outbox rows as published before each tick, so the cross-tenant claim would only
    // find this test's message. It could not work: it settles the backlog that exists at that
    // instant, and the AcceptanceApi collection — which xUnit runs in parallel against the same
    // database — keeps inserting booking.command.requested.v1 rows afterwards. It also stamped
    // those foreign rows published, corrupting a concurrent test's state. Bounding the claim to
    // this test's tenant (OutboxTenantScope) is what actually isolates the tick, and it makes the
    // sweep unnecessary: every test here mints a fresh GUID tenant with no backlog of its own.

    // ── Test A: happy path (§5.1) ────────────────────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_OutboxPublisherDrivesFullChain_ConsumerRunsOnce()
    {
        var tenantId = $"t8-e2e-a-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        const string MessageType = "booking.e2e_capstone.requested.v1";

        // Seed a BookingOperation (FK required by WorkflowStep) and an outbox row.
        string operationId;
        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId);
            seedCtx.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = messageId,
                MessageType = MessageType,
                CorrelationId = messageId,
                OperationId = operationId,
                Payload = $"{{\"operation_id\":\"{operationId}\"}}",
            });
            await seedCtx.SaveChangesAsync();
        }

        // Build the full DI container and register the test consumer factory.
        await using var sp = BuildContainer(tenantId);

        var registry = sp.GetRequiredService<ConsumerRegistry>();
        registry.Register(MessageType, env =>
            new E2EConsumerRequest(
                env.TenantId,
                env.MessageId,
                env.OperationId ?? env.MessageId));

        // Drive one publisher tick via the concrete hosted service.
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        // ── Assert: outbox row is now marked published ───────────────────────────────
        await using var assertCtx = fixture.CreateContext(tenantId);

        var outboxRow = await assertCtx.OutboxMessages
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.MessageId == messageId);
        Assert.NotNull(outboxRow);
        Assert.NotNull(outboxRow!.PublishedAt);

        // ── Assert: consumer_inbox row exists with Outcome=Admitted ─────────────────
        var inboxRow = await assertCtx.ConsumerInbox
            .AsNoTracking()
            .FirstOrDefaultAsync(i =>
                i.ConsumerName == "e2e.capstone.consumer" &&
                i.TenantId == tenantId &&
                i.MessageId == messageId);
        Assert.NotNull(inboxRow);
        Assert.Equal(InboxOutcome.Admitted, inboxRow!.Outcome);

        // ── Assert: durable side-effect (WorkflowStep) written exactly once ─────────
        var stepCount = await assertCtx.WorkflowSteps
            .IgnoreQueryFilters()
            .CountAsync(s =>
                s.TenantId == tenantId &&
                s.WorkflowStepId == $"e2e-step-{messageId}");
        Assert.Equal(1, stepCount);
    }

    // ── Test B: duplicate suppression (§5.2/§11) ────────────────────────────────────────────

    [Fact]
    public async Task DuplicateDelivery_InboxDedupsSecondPublish_ConsumerRunsOnce()
    {
        var tenantId = $"t8-e2e-b-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        const string MessageType = "booking.e2e_capstone.requested.v1";

        // Seed operation + outbox row.
        string operationId;
        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId);
            seedCtx.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = messageId,
                MessageType = MessageType,
                CorrelationId = messageId,
                OperationId = operationId,
                Payload = $"{{\"operation_id\":\"{operationId}\"}}",
            });
            await seedCtx.SaveChangesAsync();
        }

        await using var sp = BuildContainer(tenantId);

        var registry = sp.GetRequiredService<ConsumerRegistry>();
        registry.Register(MessageType, env =>
            new E2EConsumerRequest(
                env.TenantId,
                env.MessageId,
                env.OperationId ?? env.MessageId));

        var publisher = BuildPublisher(sp);

        // ── First publish (happy path) ───────────────────────────────────────────────
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        // Verify first publish admitted exactly one inbox row.
        await using (var check1 = fixture.CreateContext(tenantId))
        {
            var count = await check1.ConsumerInbox
                .AsNoTracking()
                .CountAsync(i =>
                    i.ConsumerName == "e2e.capstone.consumer" &&
                    i.TenantId == tenantId &&
                    i.MessageId == messageId);
            Assert.Equal(1, count);
        }

        // ── Simulate duplicate delivery: reset published_at + lease so the publisher
        //    picks the same row again (mirrors §5.2 crash-before-mark scenario). ──────
        await using (var resetCtx = fixture.CreateContext("e2e-dup-reset"))
        {
            await resetCtx.OutboxMessages
                .IgnoreQueryFilters()
                .Where(m => m.TenantId == tenantId && m.MessageId == messageId)
                .ExecuteUpdateAsync(b => b
                    .SetProperty(m => m.PublishedAt, (DateTimeOffset?)null)
                    .SetProperty(m => m.LeaseOwner, (string?)null)
                    .SetProperty(m => m.LeaseExpiresAt, (DateTimeOffset?)null));
        }

        // Settle THIS TENANT's other unpublished rows that appeared between the two ticks, so
        // only the duplicate row is in the second batch.
        //
        // The tenant predicate is load-bearing. Without it this stamps every OTHER tenant's
        // unpublished rows as published — the exact cross-tenant corruption that
        // ScopeOutboxClaimToTenant was introduced to end, re-entering through a raw DbContext
        // instead of the publisher. It is also unnecessary without it: the claim in this
        // container is already bounded to tenantId, so a foreign row can never reach this
        // batch to begin with.
        await using (var neutralCtx = fixture.CreateContext("e2e-neutralize-2"))
        {
            await neutralCtx.OutboxMessages
                .IgnoreQueryFilters()
                .Where(m => m.TenantId == tenantId && m.PublishedAt == null && m.MessageId != messageId)
                .ExecuteUpdateAsync(b => b
                    .SetProperty(m => m.PublishedAt, DateTimeOffset.UtcNow)
                    .SetProperty(m => m.LeaseOwner, (string?)null)
                    .SetProperty(m => m.LeaseExpiresAt, (DateTimeOffset?)null));
        }

        // ── Second publish tick (duplicate delivery) ─────────────────────────────────
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        // ── Assert: still exactly ONE consumer_inbox row ─────────────────────────────
        await using var assertCtx = fixture.CreateContext(tenantId);

        var inboxRowCount = await assertCtx.ConsumerInbox
            .AsNoTracking()
            .CountAsync(i =>
                i.ConsumerName == "e2e.capstone.consumer" &&
                i.TenantId == tenantId &&
                i.MessageId == messageId);
        Assert.Equal(1, inboxRowCount);

        // ── Assert: consumer side-effect occurred exactly once (handler not re-run) ──
        var stepCount = await assertCtx.WorkflowSteps
            .IgnoreQueryFilters()
            .CountAsync(s =>
                s.TenantId == tenantId &&
                s.WorkflowStepId == $"e2e-step-{messageId}");
        Assert.Equal(1, stepCount);
    }
}
