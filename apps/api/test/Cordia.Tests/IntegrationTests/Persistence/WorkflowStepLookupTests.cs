#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.Repositories;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class WorkflowStepLookupTests(PostgresFixture fixture)
{
    // (1) GetByOperationAndTypeAsync returns the seeded step
    [Fact]
    public async Task GetByOperationAndTypeAsync_ExistingStep_ReturnsIt()
    {
        var tenantId = $"t3-wsl-{Guid.NewGuid():N}";
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
                StepType = "capacity_hold",
                Status = WorkflowStepStatus.Pending,
                AttemptCount = 0,
                Version = 0,
            });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var repo = new WorkflowStepRepository(readCtx);
        var step = await repo.GetByOperationAndTypeAsync(tenantId, operationId, "capacity_hold");

        Assert.NotNull(step);
        Assert.Equal(stepId, step.WorkflowStepId);
        Assert.Equal("capacity_hold", step.StepType);
        Assert.Equal(operationId, step.OperationId);
    }

    // (2) GetByOperationAndTypeAsync returns null for an unknown step_type
    [Fact]
    public async Task GetByOperationAndTypeAsync_UnknownStepType_ReturnsNull()
    {
        var tenantId = $"t3-wsl-{Guid.NewGuid():N}";
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        await using var readCtx = fixture.CreateContext(tenantId);
        var repo = new WorkflowStepRepository(readCtx);
        var step = await repo.GetByOperationAndTypeAsync(tenantId, operationId, "nonexistent_step");

        Assert.Null(step);
    }

    // (3) GetByOperationAndTypeAsync returns null for a wrong tenant (tenant isolation)
    [Fact]
    public async Task GetByOperationAndTypeAsync_WrongTenant_ReturnsNull()
    {
        var tenantId = $"t3-wsl-{Guid.NewGuid():N}";
        var otherTenantId = $"t3-wsl-other-{Guid.NewGuid():N}";
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
                StepType = "capacity_hold",
                Status = WorkflowStepStatus.Pending,
                AttemptCount = 0,
                Version = 0,
            });
            await ctx.SaveChangesAsync();
        }

        // Query with wrong tenant — ambient filter + explicit predicate both exclude the row
        await using var readCtx = fixture.CreateContext(otherTenantId);
        var repo = new WorkflowStepRepository(readCtx);
        var step = await repo.GetByOperationAndTypeAsync(otherTenantId, operationId, "capacity_hold");

        Assert.Null(step);
    }
}
