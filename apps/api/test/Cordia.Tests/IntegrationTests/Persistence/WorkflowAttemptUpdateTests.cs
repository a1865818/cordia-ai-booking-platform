#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.EntityFramework.Concurrency;
using Cordia.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class WorkflowAttemptUpdateTests(PostgresFixture fixture)
{
    // (1) UpdateAsync persists mutable fields; identity fields remain unchanged
    [Fact]
    public async Task UpdateAsync_MutableFields_PersistedCorrectly()
    {
        var tenantId = $"t3-wau-{Guid.NewGuid():N}";
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        var attemptId = $"att-{Guid.NewGuid():N}";
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var completedAt = DateTimeOffset.UtcNow;

        // Insert a Planned attempt (no StartedAt/CompletedAt yet)
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.WorkflowAttempts.Add(new WorkflowAttempt
            {
                TenantId = tenantId,
                AttemptId = attemptId,
                OperationId = operationId,
                WorkflowStep = "capacity_hold",
                ProviderName = "acme",
                ProviderAccount = "acct-001",
                RequestHash = "rh-stable",
                ProviderIdempotencyKey = "pik_abc123",
                AttemptNumber = 1,
                Outcome = AttemptOutcome.Planned,
                RequestMeta = "{}",
                ResponseMeta = "{}",
            });
            await ctx.SaveChangesAsync();
        }

        // UpdateAsync: set Outcome=Succeeded, ProviderRef, StartedAt, CompletedAt
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var repo = new WorkflowAttemptRepository(ctx, new OperationDecisionLock(ctx));
            // Load then mutate (tracked instance path)
            var attempt = await ctx.WorkflowAttempts.SingleAsync(a => a.AttemptId == attemptId);
            attempt.Outcome = AttemptOutcome.Succeeded;
            attempt.ProviderRef = "prov-ref-001";
            attempt.StartedAt = startedAt;
            attempt.CompletedAt = completedAt;
            attempt.ResponseMeta = """{"status":"ok"}""";
            await repo.UpdateAsync(attempt);
        }

        // Re-read and assert mutable fields changed; identity fields unchanged
        await using var readCtx = fixture.CreateContext(tenantId);
        var updated = await readCtx.WorkflowAttempts.AsNoTracking().SingleAsync(a => a.AttemptId == attemptId);

        Assert.Equal(AttemptOutcome.Succeeded, updated.Outcome);
        Assert.Equal("prov-ref-001", updated.ProviderRef);
        Assert.NotNull(updated.StartedAt);
        Assert.NotNull(updated.CompletedAt);
        // Identity fields must be unchanged
        Assert.Equal(attemptId, updated.AttemptId);
        Assert.Equal(1, updated.AttemptNumber);
        Assert.Equal("rh-stable", updated.RequestHash);
        Assert.Equal("pik_abc123", updated.ProviderIdempotencyKey);
        Assert.Equal("acme", updated.ProviderName);
        Assert.Equal("acct-001", updated.ProviderAccount);
    }

    // (2) GetLatestForStepAsync returns the highest-AttemptNumber attempt
    [Fact]
    public async Task GetLatestForStepAsync_TwoAttempts_ReturnsHighestAttemptNumber()
    {
        var tenantId = $"t3-wau-{Guid.NewGuid():N}";
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        var attemptId1 = $"att-{Guid.NewGuid():N}";
        var attemptId2 = $"att-{Guid.NewGuid():N}";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.WorkflowAttempts.AddRange(
                new WorkflowAttempt
                {
                    TenantId = tenantId,
                    AttemptId = attemptId1,
                    OperationId = operationId,
                    WorkflowStep = "capacity_hold",
                    ProviderName = "acme",
                    ProviderAccount = "acct-001",
                    RequestHash = "rh-001",
                    AttemptNumber = 1,
                    Outcome = AttemptOutcome.TransientFailure,
                    RequestMeta = "{}",
                    ResponseMeta = "{}",
                },
                new WorkflowAttempt
                {
                    TenantId = tenantId,
                    AttemptId = attemptId2,
                    OperationId = operationId,
                    WorkflowStep = "capacity_hold",
                    ProviderName = "acme",
                    ProviderAccount = "acct-001",
                    RequestHash = "rh-002",
                    AttemptNumber = 2,
                    Outcome = AttemptOutcome.Succeeded,
                    RequestMeta = "{}",
                    ResponseMeta = "{}",
                }
            );
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var repo = new WorkflowAttemptRepository(readCtx, new OperationDecisionLock(readCtx));
        var latest = await repo.GetLatestForStepAsync(tenantId, operationId, "capacity_hold");

        Assert.NotNull(latest);
        Assert.Equal(attemptId2, latest.AttemptId);
        Assert.Equal(2, latest.AttemptNumber);
        Assert.Equal(AttemptOutcome.Succeeded, latest.Outcome);
    }

    // (3) GetLatestForStepAsync returns null when no attempts exist for the step
    [Fact]
    public async Task GetLatestForStepAsync_NoAttempts_ReturnsNull()
    {
        var tenantId = $"t3-wau-{Guid.NewGuid():N}";
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        await using var readCtx = fixture.CreateContext(tenantId);
        var repo = new WorkflowAttemptRepository(readCtx, new OperationDecisionLock(readCtx));
        var latest = await repo.GetLatestForStepAsync(tenantId, operationId, "capacity_hold");

        Assert.Null(latest);
    }
}
