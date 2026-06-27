#pragma warning disable xUnit1051
using Cordia.Application.Extensions;
using Cordia.Application.Handlers.BookingFulfillment.Sla;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Extensions;
using Cordia.Tests.IntegrationTests.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cordia.Tests.IntegrationTests.Messaging;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class SlaEvaluatorTests(PostgresFixture fixture)
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
                ["Messaging:Sla:Enabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddApplication();
        services.AddInfrastructure(config);
        return services.BuildServiceProvider();
    }

    // Helper: seed an operation with a specific status and UpdatedAt
    private async Task<string> SeedOperationWithUpdatedAtAsync(
        string tenantId, OperationStatus status, DateTimeOffset updatedAt)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        var operationId = await TestData.SeedOperationAsync(ctx, tenantId, status);
        // Override UpdatedAt
        await ctx.BookingOperations
            .IgnoreQueryFilters()
            .Where(o => o.OperationId == operationId)
            .ExecuteUpdateAsync(b => b.SetProperty(o => o.UpdatedAt, updatedAt));
        return operationId;
    }

    // Test 1: Escalation - creates work item + escalation notification
    [Fact]
    public async Task EvaluateOperationSlaCommand_Escalation_CreatesWorkItemAndNotification()
    {
        var tenantId = $"t9-esc-{Guid.NewGuid():N}";
        var operationId = await SeedOperationWithUpdatedAtAsync(
            tenantId, OperationStatus.Finalizing, DateTimeOffset.UtcNow.AddMinutes(-20));

        await using var sp = BuildContainer();
        using var scope = sp.CreateScope();
        // Set tenant context
        var tenantContext = scope.ServiceProvider.GetRequiredService<Cordia.Infrastructure.Services.TenantContext>();
        tenantContext.TenantId = tenantId;
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await mediator.Send(new EvaluateOperationSlaCommand(tenantId, operationId));

        // Assert: work item created
        await using var assertCtx = fixture.CreateContext(tenantId);
        var workItem = await assertCtx.OperationWorkItems
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.OperationId == operationId);
        Assert.NotNull(workItem);
        Assert.Equal("open", workItem.State);
        Assert.Equal("sla_escalation", workItem.Reason);

        // Assert: escalation outbox row
        var escalationMsg = await assertCtx.OutboxMessages
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.MessageId == $"obx_sla_esc_{operationId}");
        Assert.NotNull(escalationMsg);
        Assert.Equal("notification.sla_escalation.v1", escalationMsg.MessageType);
    }

    // Test 2: Pending only - creates still_confirming notification but NO work item
    [Fact]
    public async Task EvaluateOperationSlaCommand_PendingOnly_CreatesStillConfirmingNotification()
    {
        var tenantId = $"t9-pend-{Guid.NewGuid():N}";
        var operationId = await SeedOperationWithUpdatedAtAsync(
            tenantId, OperationStatus.Finalizing, DateTimeOffset.UtcNow.AddSeconds(-45));

        await using var sp = BuildContainer();
        using var scope = sp.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<Cordia.Infrastructure.Services.TenantContext>();
        tenantContext.TenantId = tenantId;
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await mediator.Send(new EvaluateOperationSlaCommand(tenantId, operationId));

        await using var assertCtx = fixture.CreateContext(tenantId);

        // No work item
        var workItem = await assertCtx.OperationWorkItems
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.OperationId == operationId);
        Assert.Null(workItem);

        // still_confirming notification
        var pendingMsg = await assertCtx.OutboxMessages
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.MessageId == $"obx_sla_pending_{operationId}");
        Assert.NotNull(pendingMsg);
        Assert.Equal("notification.still_confirming.v1", pendingMsg.MessageType);
    }

    // Test 3: User-wait state - no action
    [Fact]
    public async Task EvaluateOperationSlaCommand_UserWaitState_NoAction()
    {
        var tenantId = $"t9-uwait-{Guid.NewGuid():N}";
        var operationId = await SeedOperationWithUpdatedAtAsync(
            tenantId, OperationStatus.AwaitingPayment, DateTimeOffset.UtcNow.AddMinutes(-20));

        await using var sp = BuildContainer();
        using var scope = sp.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<Cordia.Infrastructure.Services.TenantContext>();
        tenantContext.TenantId = tenantId;
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await mediator.Send(new EvaluateOperationSlaCommand(tenantId, operationId));

        await using var assertCtx = fixture.CreateContext(tenantId);
        var workItem = await assertCtx.OperationWorkItems
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.OperationId == operationId);
        Assert.Null(workItem);

        var anyMsg = await assertCtx.OutboxMessages
            .IgnoreQueryFilters()
            .AnyAsync(m => m.OperationId == operationId &&
                (m.MessageType == "notification.sla_escalation.v1" || m.MessageType == "notification.still_confirming.v1"));
        Assert.False(anyMsg);
    }

    // Test 4: Terminal state - no action
    [Fact]
    public async Task EvaluateOperationSlaCommand_TerminalState_NoAction()
    {
        var tenantId = $"t9-term-{Guid.NewGuid():N}";
        var operationId = await SeedOperationWithUpdatedAtAsync(
            tenantId, OperationStatus.Booked, DateTimeOffset.UtcNow.AddMinutes(-20));

        await using var sp = BuildContainer();
        using var scope = sp.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<Cordia.Infrastructure.Services.TenantContext>();
        tenantContext.TenantId = tenantId;
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        await mediator.Send(new EvaluateOperationSlaCommand(tenantId, operationId));

        await using var assertCtx = fixture.CreateContext(tenantId);
        var workItem = await assertCtx.OperationWorkItems
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.OperationId == operationId);
        Assert.Null(workItem);
    }

    // Test 5: Idempotency - run twice, exactly ONE work item + ONE escalation notification
    [Fact]
    public async Task EvaluateOperationSlaCommand_Idempotency_NoDuplicates()
    {
        var tenantId = $"t9-idem-{Guid.NewGuid():N}";
        var operationId = await SeedOperationWithUpdatedAtAsync(
            tenantId, OperationStatus.Finalizing, DateTimeOffset.UtcNow.AddMinutes(-20));

        await using var sp = BuildContainer();

        // First run
        using (var scope = sp.CreateScope())
        {
            var tenantContext = scope.ServiceProvider.GetRequiredService<Cordia.Infrastructure.Services.TenantContext>();
            tenantContext.TenantId = tenantId;
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            await mediator.Send(new EvaluateOperationSlaCommand(tenantId, operationId));
        }

        // Second run
        using (var scope = sp.CreateScope())
        {
            var tenantContext = scope.ServiceProvider.GetRequiredService<Cordia.Infrastructure.Services.TenantContext>();
            tenantContext.TenantId = tenantId;
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            await mediator.Send(new EvaluateOperationSlaCommand(tenantId, operationId));
        }

        await using var assertCtx = fixture.CreateContext(tenantId);

        // Exactly ONE work item
        var workItemCount = await assertCtx.OperationWorkItems
            .IgnoreQueryFilters()
            .CountAsync(w => w.OperationId == operationId && w.State == "open" && w.Reason == "sla_escalation");
        Assert.Equal(1, workItemCount);

        // Exactly ONE escalation outbox row
        var escalationCount = await assertCtx.OutboxMessages
            .IgnoreQueryFilters()
            .CountAsync(m => m.MessageId == $"obx_sla_esc_{operationId}");
        Assert.Equal(1, escalationCount);
    }
}
