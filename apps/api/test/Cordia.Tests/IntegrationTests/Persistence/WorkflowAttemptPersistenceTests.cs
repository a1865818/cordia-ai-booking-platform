#pragma warning disable xUnit1051
using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Persistence;

/// <summary>Compares two JSON strings for value-equality, ignoring whitespace differences (jsonb normalises spacing).</summary>
file static class JsonAssert
{
    private static readonly JsonSerializerOptions s_compact = new() { WriteIndented = false };

    public static void Equal(string expected, string actual)
    {
        var e = JsonSerializer.Serialize(JsonDocument.Parse(expected).RootElement, s_compact);
        var a = JsonSerializer.Serialize(JsonDocument.Parse(actual).RootElement, s_compact);
        Assert.Equal(e, a);
    }
}

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class WorkflowAttemptPersistenceTests(PostgresFixture fixture)
{
    // (1) Insert attempt with enum + jsonb round-trip
    [Fact]
    public async Task Insert_ThenGetAsync_RoundTripsEnumsAndJsonb()
    {
        var tenantId = $"t2-wa-{Guid.NewGuid():N}";
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        var attemptId = $"att-{Guid.NewGuid():N}";
        var reqMeta = """{"why":"timeout"}""";
        var resMeta = """{"code":503}""";
        var reconciliation = """{"actor":"manual"}""";

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
                RequestHash = "rh-001",
                AttemptNumber = 1,
                Outcome = AttemptOutcome.Ambiguous,
                RetryClassification = RetryClassification.Transient,
                RequestMeta = reqMeta,
                ResponseMeta = resMeta,
                Reconciliation = reconciliation,
            });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var attempt = await readCtx.WorkflowAttempts.AsNoTracking().SingleAsync(a => a.AttemptId == attemptId);
        Assert.Equal(AttemptOutcome.Ambiguous, attempt.Outcome);
        Assert.Equal(RetryClassification.Transient, attempt.RetryClassification);
        // jsonb normalises spacing; compare as parsed JSON documents
        JsonAssert.Equal(reqMeta, attempt.RequestMeta);
        JsonAssert.Equal(resMeta, attempt.ResponseMeta);
        JsonAssert.Equal(reconciliation, attempt.Reconciliation!);
    }

    // (2) Two attempts for same operation insert fine (append-only, no unique conflict)
    [Fact]
    public async Task TwoAttempts_SameOperation_BothInsertSuccessfully()
    {
        var tenantId = $"t2-wa-{Guid.NewGuid():N}";
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.WorkflowAttempts.Add(new WorkflowAttempt
            {
                TenantId = tenantId,
                AttemptId = $"att-{Guid.NewGuid():N}",
                OperationId = operationId,
                WorkflowStep = "capacity_hold",
                ProviderName = "acme",
                ProviderAccount = "acct-001",
                RequestHash = "rh-001",
                AttemptNumber = 1,
                Outcome = AttemptOutcome.TransientFailure,
                RequestMeta = "{}",
                ResponseMeta = "{}",
            });
            ctx.WorkflowAttempts.Add(new WorkflowAttempt
            {
                TenantId = tenantId,
                AttemptId = $"att-{Guid.NewGuid():N}",
                OperationId = operationId,
                WorkflowStep = "capacity_hold",
                ProviderName = "acme",
                ProviderAccount = "acct-001",
                RequestHash = "rh-002",
                AttemptNumber = 2,
                Outcome = AttemptOutcome.Succeeded,
                RequestMeta = "{}",
                ResponseMeta = "{}",
            });
            await ctx.SaveChangesAsync(); // Both must succeed
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var count = await readCtx.WorkflowAttempts.CountAsync(a => a.OperationId == operationId);
        Assert.Equal(2, count);
    }
}
