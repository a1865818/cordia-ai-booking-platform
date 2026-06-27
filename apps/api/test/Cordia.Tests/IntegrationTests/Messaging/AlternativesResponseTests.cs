#pragma warning disable xUnit1051
using Cordia.Application.Extensions;
using Cordia.Application.Handlers.BookingFulfillment.Alternatives;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Infrastructure.Extensions;
using Cordia.Infrastructure.Services;
using Cordia.Tests.IntegrationTests.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cordia.Tests.IntegrationTests.Messaging;

/// <summary>
/// Integration tests for <c>AlternativesRequired → Waitlisted / FailedFinal</c> customer-response
/// path (spec 06 §6.1, §11, T11a).
///
/// Scenarios:
///   1. Waitlist: seed AlternativesRequired op → RespondToAlternativesCommand(Waitlist)
///      → op→Waitlisted + notification.waitlisted.v1 outbox + operation.waitlisted audit.
///   2. Decline: seed AlternativesRequired op → RespondToAlternativesCommand(Decline)
///      → op→FailedFinal + notification.booking_failed.v1 outbox + operation.failed_final audit.
///   3. Guard: send on a non-AlternativesRequired op → OperationConflictException.
///   4. Guard: not found → OperationConflictException.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class AlternativesResponseTests(PostgresFixture fixture)
{
    private static ServiceProvider BuildContainer()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CordiaDBConnection"] = PostgresFixture.TestConnStr,
                ["Messaging:Publisher:Enabled"] = "false",
                ["Messaging:ScheduledJobPoller:Enabled"] = "false",
                ["Messaging:StepSweeper:Enabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddApplication();
        services.AddInfrastructure(config);

        return services.BuildServiceProvider();
    }

    // ── Scenario 1: Waitlist ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Waitlist_FromAlternativesRequired_TransitionsToWaitlisted_EmitsNotificationAndAudit()
    {
        var tenantId = $"t11-alt-waitlist-{Guid.NewGuid():N}";
        string operationId;

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.AlternativesRequired);
        }

        await using var sp = BuildContainer();
        using var scope = sp.CreateScope();

        // Must set TenantContext so the global EF query filter finds the seeded rows.
        var tenantCtx = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenantCtx.TenantId = tenantId;

        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Send(new RespondToAlternativesCommand(tenantId, operationId, AlternativesResponse.Waitlist));

        await using var assertCtx = fixture.CreateContext(tenantId);

        // ── Assert: op → Waitlisted ──────────────────────────────────────────────────────────
        var op = await assertCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(op);
        Assert.Equal(OperationStatus.Waitlisted, op!.Status);
        Assert.Equal("waitlisted", op.StatusReason);

        // ── Assert: notification.waitlisted.v1 outbox row ───────────────────────────────────
        var notification = await assertCtx.OutboxMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId &&
                m.OperationId == operationId &&
                m.MessageType == "notification.waitlisted.v1");

        Assert.NotNull(notification);

        // ── Assert: operation.waitlisted audit event ─────────────────────────────────────────
        var audit = await assertCtx.AuditEvents
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e =>
                e.TenantId == tenantId &&
                e.OperationId == operationId &&
                e.EventType == "operation.waitlisted");

        Assert.NotNull(audit);
    }

    // ── Scenario 2: Decline ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Decline_FromAlternativesRequired_TransitionsToFailedFinal_EmitsNotificationAndAudit()
    {
        var tenantId = $"t11-alt-decline-{Guid.NewGuid():N}";
        string operationId;

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.AlternativesRequired);
        }

        await using var sp = BuildContainer();
        using var scope = sp.CreateScope();

        // Must set TenantContext so the global EF query filter finds the seeded rows.
        var tenantCtx = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenantCtx.TenantId = tenantId;

        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        await mediator.Send(new RespondToAlternativesCommand(tenantId, operationId, AlternativesResponse.Decline));

        await using var assertCtx = fixture.CreateContext(tenantId);

        // ── Assert: op → FailedFinal ─────────────────────────────────────────────────────────
        var op = await assertCtx.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        Assert.NotNull(op);
        Assert.Equal(OperationStatus.FailedFinal, op!.Status);
        Assert.Equal("alternatives_declined", op.StatusReason);

        // ── Assert: notification.booking_failed.v1 outbox row ───────────────────────────────
        var notification = await assertCtx.OutboxMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m =>
                m.TenantId == tenantId &&
                m.OperationId == operationId &&
                m.MessageType == "notification.booking_failed.v1");

        Assert.NotNull(notification);

        // ── Assert: operation.failed_final audit event ────────────────────────────────────────
        var audit = await assertCtx.AuditEvents
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e =>
                e.TenantId == tenantId &&
                e.OperationId == operationId &&
                e.EventType == "operation.failed_final");

        Assert.NotNull(audit);
    }

    // ── Scenario 3: Guard — wrong status ────────────────────────────────────────────────────

    [Fact]
    public async Task Respond_OnNonAlternativesRequiredOp_ThrowsOperationConflictException()
    {
        var tenantId = $"t11-alt-guard-{Guid.NewGuid():N}";
        string operationId;

        // Seed in Accepted (not AlternativesRequired).
        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Accepted);
        }

        await using var sp = BuildContainer();
        using var scope = sp.CreateScope();

        var tenantCtx = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenantCtx.TenantId = tenantId;

        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            mediator.Send(new RespondToAlternativesCommand(tenantId, operationId, AlternativesResponse.Waitlist)));
    }

    // ── Scenario 4: Guard — not found / tenant mismatch ─────────────────────────────────────

    [Fact]
    public async Task Respond_OnUnknownOperation_ThrowsOperationConflictException()
    {
        var tenantId = $"t11-alt-notfound-{Guid.NewGuid():N}";
        var nonExistentOpId = $"op-does-not-exist-{Guid.NewGuid():N}";

        await using var sp = BuildContainer();
        using var scope = sp.CreateScope();

        var tenantCtx = scope.ServiceProvider.GetRequiredService<TenantContext>();
        tenantCtx.TenantId = tenantId;

        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await Assert.ThrowsAsync<OperationConflictException>(() =>
            mediator.Send(new RespondToAlternativesCommand(tenantId, nonExistentOpId, AlternativesResponse.Waitlist)));
    }
}
