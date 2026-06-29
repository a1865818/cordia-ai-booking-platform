#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.EntityFramework.Concurrency;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class WorkflowStepPersistenceTests(PostgresFixture fixture)
{
    // (1) unique-per-(operation_id, step_type): second insert with same (operation_id, step_type) throws UniqueViolation
    [Fact]
    public async Task Insert_DuplicateStepType_ThrowsUniqueViolation()
    {
        var tenantId = $"t2-ws-{Guid.NewGuid():N}";
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.WorkflowSteps.Add(new WorkflowStep
            {
                TenantId = tenantId,
                WorkflowStepId = $"ws-{Guid.NewGuid():N}",
                OperationId = operationId,
                StepType = "capacity_hold",
                Status = WorkflowStepStatus.Pending,
                AttemptCount = 0,
                Version = 0,
            });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = fixture.CreateContext(tenantId);
        ctx2.WorkflowSteps.Add(new WorkflowStep
        {
            TenantId = tenantId,
            WorkflowStepId = $"ws-{Guid.NewGuid():N}",
            OperationId = operationId,
            StepType = "capacity_hold", // same step_type for same operation → unique violation
            Status = WorkflowStepStatus.Pending,
            AttemptCount = 0,
            Version = 0,
        });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx2.SaveChangesAsync());
        var pgEx = ex.InnerException as PostgresException;
        Assert.NotNull(pgEx);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, pgEx.SqlState);
    }

    // (2) version-token: two contexts both try TryTransitionStatus from version 0 → exactly one wins
    [Fact]
    public async Task TryTransitionStatus_ExactlyOneWins()
    {
        var tenantId = $"t2-ws-{Guid.NewGuid():N}";
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        var stepId = $"ws-{Guid.NewGuid():N}";
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.WorkflowSteps.Add(new WorkflowStep
            {
                TenantId = tenantId,
                WorkflowStepId = stepId,
                OperationId = operationId,
                StepType = "payment",
                Status = WorkflowStepStatus.Pending,
                AttemptCount = 0,
                Version = 0,
            });
            await ctx.SaveChangesAsync();
        }

        await using var ctx1 = fixture.CreateContext(tenantId);
        await using var ctx2 = fixture.CreateContext(tenantId);

        var rows1 = await VersionTokenUpdate.UpdateAsync<WorkflowStep>(
            ctx1, expectedVersion: 0,
            e => e.TenantId == tenantId && e.WorkflowStepId == stepId,
            s => s.SetProperty(x => x.Status, WorkflowStepStatus.InProgress)
                  .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow));

        var rows2 = await VersionTokenUpdate.UpdateAsync<WorkflowStep>(
            ctx2, expectedVersion: 0,
            e => e.TenantId == tenantId && e.WorkflowStepId == stepId,
            s => s.SetProperty(x => x.Status, WorkflowStepStatus.InProgress)
                  .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow));

        Assert.Equal(1, rows1 + rows2);
        Assert.True(VersionTokenUpdate.ShouldReload(rows1) || VersionTokenUpdate.ShouldReload(rows2));

        await using var verifyCtx = fixture.CreateContext(tenantId);
        var reloaded = await verifyCtx.WorkflowSteps
            .AsNoTracking()
            .SingleAsync(s => s.WorkflowStepId == stepId);

        Assert.Equal(WorkflowStepStatus.InProgress, reloaded.Status);
        Assert.Equal(1L, reloaded.Version);
    }

    // (3) enum round-trip
    [Fact]
    public async Task Status_EnumRoundTrip()
    {
        var tenantId = $"t2-ws-{Guid.NewGuid():N}";
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        var stepId = $"ws-{Guid.NewGuid():N}";
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.WorkflowSteps.Add(new WorkflowStep
            {
                TenantId = tenantId,
                WorkflowStepId = stepId,
                OperationId = operationId,
                StepType = "finalize",
                Status = WorkflowStepStatus.Succeeded,
                AttemptCount = 2,
                Version = 0,
            });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var step = await readCtx.WorkflowSteps.AsNoTracking().SingleAsync(s => s.WorkflowStepId == stepId);
        Assert.Equal(WorkflowStepStatus.Succeeded, step.Status);
        Assert.Equal(2, step.AttemptCount);
    }
}
