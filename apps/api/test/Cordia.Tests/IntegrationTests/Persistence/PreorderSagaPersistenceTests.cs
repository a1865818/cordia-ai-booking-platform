#pragma warning disable xUnit1051
using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.EntityFramework.Concurrency;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class PreorderSagaPersistenceTests(PostgresFixture fixture)
{
    // (1) Seed operation, insert saga (state=Revalidating, preorder_reference jsonb), read back state + jsonb.
    [Fact]
    public async Task Insert_Saga_RoundTripsStateEnumAndJsonb()
    {
        var tenantId = $"t5-saga-{Guid.NewGuid():N}";
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        var sagaId = $"saga-{Guid.NewGuid():N}";
        const string preorderRef = """{"catalog_version":"menu-2026-v1","lines":[{"item_id":"item-1","quantity":2,"captured_price_amount_minor":3200,"captured_price_currency":"AUD"}],"confirmation_id":"conf-001"}""";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.PreorderSagas.Add(new PreorderSaga
            {
                TenantId = tenantId,
                SagaId = sagaId,
                OperationId = operationId,
                SagaState = PreorderSagaState.Revalidating,
                PreorderReference = preorderRef,
                Version = 0,
            });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var loaded = await readCtx.PreorderSagas.AsNoTracking()
            .SingleAsync(s => s.SagaId == sagaId);

        Assert.Equal(PreorderSagaState.Revalidating, loaded.SagaState);

        // jsonb round-trip: PostgreSQL normalizes key order; compare via parsed JSON.
        Assert.NotNull(loaded.PreorderReference);
        var doc = JsonDocument.Parse(loaded.PreorderReference);
        Assert.Equal("menu-2026-v1", doc.RootElement.GetProperty("catalog_version").GetString());
        Assert.Equal("conf-001", doc.RootElement.GetProperty("confirmation_id").GetString());
        var lines = doc.RootElement.GetProperty("lines").EnumerateArray().ToList();
        Assert.Single(lines);
        Assert.Equal("item-1", lines[0].GetProperty("item_id").GetString());
        Assert.Equal(2, lines[0].GetProperty("quantity").GetInt32());
    }

    // (2) Version-token: Revalidating → HoldingCapacity, exactly one wins.
    [Fact]
    public async Task TryTransition_VersionToken_ExactlyOneWins()
    {
        var tenantId = $"t5-saga-vt-{Guid.NewGuid():N}";
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        var sagaId = $"saga-{Guid.NewGuid():N}";
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.PreorderSagas.Add(new PreorderSaga
            {
                TenantId = tenantId,
                SagaId = sagaId,
                OperationId = operationId,
                SagaState = PreorderSagaState.Revalidating,
                PreorderReference = """{"catalog_version":"v1","lines":[]}""",
                Version = 0,
            });
            await ctx.SaveChangesAsync();
        }

        await using var ctx1 = fixture.CreateContext(tenantId);
        await using var ctx2 = fixture.CreateContext(tenantId);

        var rows1 = await VersionTokenUpdate.UpdateAsync<PreorderSaga>(
            ctx1, expectedVersion: 0,
            e => e.TenantId == tenantId && e.SagaId == sagaId,
            s => s.SetProperty(x => x.SagaState, PreorderSagaState.HoldingCapacity));

        var rows2 = await VersionTokenUpdate.UpdateAsync<PreorderSaga>(
            ctx2, expectedVersion: 0,
            e => e.TenantId == tenantId && e.SagaId == sagaId,
            s => s.SetProperty(x => x.SagaState, PreorderSagaState.HoldingCapacity));

        Assert.Equal(1, rows1 + rows2);

        await using var verifyCtx = fixture.CreateContext(tenantId);
        var reloaded = await verifyCtx.PreorderSagas.AsNoTracking()
            .SingleAsync(s => s.SagaId == sagaId);
        Assert.Equal(PreorderSagaState.HoldingCapacity, reloaded.SagaState);
        Assert.Equal(1L, reloaded.Version);
    }
}
