#pragma warning disable xUnit1051
using Cordia.Application.Extensions;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
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

// ── Fake validation policy ───────────────────────────────────────────────────────────────────

/// <summary>
/// IValidationPolicy stub that always rejects with a configurable reason (T11a §6.1 tests).
/// </summary>
file sealed class AlwaysRejectPolicy(string reason) : IValidationPolicy
{
    public Task<string?> CheckAsync(string tenantId, string operationId, CancellationToken ct = default)
        => Task.FromResult<string?>(reason);
}

// ── Tests ─────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Integration tests for the Validating→Rejected transition (spec 06 §6.1, T11a).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class RejectionTests(PostgresFixture fixture)
{
    private static ServiceProvider BuildContainer(string tenantId, IValidationPolicy? fakePolicy = null)
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

        if (fakePolicy is not null)
            services.AddSingleton<IValidationPolicy>(fakePolicy);

        return services.BuildServiceProvider();
    }

    private static OutboxPublisherHostedService BuildPublisher(IServiceProvider sp)
    {
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var options = sp.GetRequiredService<IOptions<MessagingOptions>>();
        return new OutboxPublisherHostedService(
            scopeFactory, options, NullLogger<OutboxPublisherHostedService>.Instance);
    }

    // There used to be a NeutralizeExistingUnpublishedAsync() here that stamped EVERY tenant's
    // unpublished outbox rows as published before each tick, so the cross-tenant claim would only
    // find this test's message. It could not work: it settles the backlog that exists at that
    // instant, and the AcceptanceApi collection — which xUnit runs in parallel against the same
    // database — keeps inserting booking.command.requested.v1 rows afterwards. It also stamped
    // those foreign rows published, corrupting a concurrent test's state. Bounding the claim to
    // this test's tenant (OutboxTenantScope) is what actually isolates the tick, and it makes the
    // sweep unnecessary: every test here mints a fresh GUID tenant with no backlog of its own.

    /// <summary>
    /// Fake IValidationPolicy returns a rejection reason → op→Rejected,
    /// notification.rejected.v1 outbox, operation.rejected audit (spec 06 §6.1, §11).
    /// </summary>
    [Fact]
    public async Task ValidationPolicyRejects_OpTransitionsToRejected_WithNotificationAndAudit()
    {
        var tenantId = $"t11-reject-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        string operationId;

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

        var fakePolicy = new AlwaysRejectPolicy("underage_guest_policy");

        await using var sp = BuildContainer(tenantId, fakePolicy: fakePolicy);
        var publisher = BuildPublisher(sp);

        // One publisher tick: Accepted → Validating → policy rejects → Rejected.
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);

        // ── Assert: op → Rejected (terminal) ────────────────────────────────────────────────
        var op = await assertCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(op);
        Assert.Equal(OperationStatus.Rejected, op!.Status);
        Assert.Equal("underage_guest_policy", op.StatusReason);

        // ── Assert: notification.rejected.v1 outbox row ──────────────────────────────────────
        var notification = await assertCtx.OutboxMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId &&
                m.OperationId == operationId &&
                m.MessageType == "notification.rejected.v1");

        Assert.NotNull(notification);

        // ── Assert: operation.rejected audit event ────────────────────────────────────────────
        var audit = await assertCtx.AuditEvents
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e =>
                e.TenantId == tenantId &&
                e.OperationId == operationId &&
                e.EventType == "operation.rejected");

        Assert.NotNull(audit);
    }

    /// <summary>
    /// Default policy (AlwaysValidPolicy): op proceeds past Validating to ReservingCapacity
    /// (eventually Booked on the happy path). No rejection.
    /// </summary>
    [Fact]
    public async Task AlwaysValidPolicy_OpProceedsPastValidating_NoRejection()
    {
        var tenantId = $"t11-valid-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        string operationId;

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

        // No override → default AlwaysValidPolicy.
        await using var sp = BuildContainer(tenantId);
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);

        var op = await assertCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(op);
        Assert.NotEqual(OperationStatus.Rejected, op!.Status);
    }
}
