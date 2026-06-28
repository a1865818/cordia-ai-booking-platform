#pragma warning disable xUnit1051
using Cordia.Application.Extensions;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
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

/// <summary>
/// End-to-end happy-path integration test for the booking fulfillment spine (T4, spec 06 §6.2).
///
/// Drives the full chain:
///   outbox row (booking.command.requested.v1) → OutboxPublisherHostedService.PublishPendingBatchAsync
///     → PostgresMessageTransport → MediatRQueueConsumerDispatcher
///     → AdvanceBookingOperationCommandHandler (driver loop)
///     → ValidateOperationCommand → ReserveCapacityCommand → FinalizeBookingCommand
///     → StubBookingProviderAdapter
///     → TXN2: op Booked + finalize step Succeeded + attempt Succeeded + outbox notification + audit
///
/// Asserts:
///   - op.Status == Booked with expected BookingId and StatusReason.
///   - finalize workflow_step has Status == Succeeded.
///   - workflow_attempt has Outcome == Succeeded with ProviderRef set, AttemptNumber == 1.
///   - notification.booking_confirmed.v1 outbox row exists.
///   - operation.booked audit event exists.
///
/// ONE publisher tick is sufficient because the driver loop advances Accepted → Validating →
/// ReservingCapacity → Held → Finalizing → Booked entirely in-process.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class FinalizeHappyPathTests(PostgresFixture fixture)
{
    // ── Container builder ────────────────────────────────────────────────────────────────────

    private static ServiceProvider BuildContainer()
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

        return services.BuildServiceProvider();
    }

    private static OutboxPublisherHostedService BuildPublisher(IServiceProvider sp)
    {
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var options = sp.GetRequiredService<IOptions<MessagingOptions>>();
        return new OutboxPublisherHostedService(
            scopeFactory,
            options,
            NullLogger<OutboxPublisherHostedService>.Instance);
    }

    // ── Neutralize helper ────────────────────────────────────────────────────────────────────

    private async Task NeutralizeExistingUnpublishedAsync()
    {
        await using var ctx = fixture.CreateContext("t4-cleanup");
        await ctx.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.PublishedAt == null)
            .ExecuteUpdateAsync(b => b
                .SetProperty(m => m.PublishedAt, DateTimeOffset.UtcNow)
                .SetProperty(m => m.LeaseOwner, (string?)null)
                .SetProperty(m => m.LeaseExpiresAt, (DateTimeOffset?)null));
    }

    // ── Happy path: single publisher tick reaches Booked ────────────────────────────────────

    [Fact]
    public async Task HappyPath_SinglePublisherTick_OperationReachesBooked()
    {
        await NeutralizeExistingUnpublishedAsync();

        var tenantId = $"t4-happy-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";

        // Seed operation (Accepted) + booking.command.requested.v1 outbox row.
        string operationId;
        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId);

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

        // Drive one publisher tick — the in-process driver loop should advance to Booked.
        await using var sp = BuildContainer();
        var publisher = BuildPublisher(sp);
        await publisher.PublishPendingBatchAsync(CancellationToken.None);

        // ── Assert: operation is Booked ─────────────────────────────────────────────────────
        await using var assertCtx = fixture.CreateContext(tenantId);

        var op = await assertCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(op);
        Assert.Equal(OperationStatus.Booked, op!.Status);
        Assert.Equal("booked", op.StatusReason);
        Assert.NotNull(op.BookingId);

        // The stub generates "ext_{providerIdempotencyKey}" as the reference.
        var expectedProviderKey = ProviderIdempotencyKey.Derive(operationId, "finalize", 1 /* revision */);
        var expectedBookingId = $"ext_{expectedProviderKey}";
        Assert.Equal(expectedBookingId, op.BookingId);

        // ── Assert: finalize workflow_step succeeded ────────────────────────────────────────
        var step = await assertCtx.WorkflowSteps
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.OperationId == operationId && s.StepType == "finalize");

        Assert.NotNull(step);
        Assert.Equal(WorkflowStepStatus.Succeeded, step!.Status);

        // ── Assert: workflow_attempt succeeded ─────────────────────────────────────────────
        var attempt = await assertCtx.WorkflowAttempts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.OperationId == operationId && a.WorkflowStep == "finalize");

        Assert.NotNull(attempt);
        Assert.Equal(AttemptOutcome.Succeeded, attempt!.Outcome);
        Assert.Equal(expectedBookingId, attempt.ProviderRef);
        Assert.Equal(1, attempt.AttemptNumber);

        // ── Assert: notification.booking_confirmed.v1 outbox row exists ────────────────────
        var notification = await assertCtx.OutboxMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId &&
                m.OperationId == operationId &&
                m.MessageType == "notification.booking_confirmed.v1");

        Assert.NotNull(notification);

        // ── Assert: operation.booked audit event exists ─────────────────────────────────────
        var audit = await assertCtx.AuditEvents
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e =>
                e.TenantId == tenantId &&
                e.OperationId == operationId &&
                e.EventType == "operation.booked");

        Assert.NotNull(audit);
    }
}
