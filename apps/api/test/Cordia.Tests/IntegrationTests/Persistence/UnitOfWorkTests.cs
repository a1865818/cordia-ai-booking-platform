#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken
using Cordia.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Persistence;

/// <summary>
/// Verifies that <see cref="UnitOfWork"/> provides atomic commit and rollback (spec 04 §10.3, Inv-4).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class UnitOfWorkTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ExecuteInTransactionAsync_CommitsWork_WhenWorkSucceeds()
    {
        var tenantId = $"t-uow-{Guid.NewGuid():N}";
        var capturedId = string.Empty;

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var uow = new UnitOfWork(ctx);
            await uow.ExecuteInTransactionAsync(async ct =>
            {
                capturedId = await TestData.SeedOperationAsync(ctx, tenantId, ct: ct);
            });
        }

        // Open a FRESH context to verify the row persisted (Inv-4 commit path).
        await using var freshCtx = fixture.CreateContext(tenantId);
        var persisted = await freshCtx.BookingOperations
            .IgnoreQueryFilters()
            .AnyAsync(o => o.TenantId == tenantId && o.OperationId == capturedId);

        Assert.True(persisted, "Operation row must be durably committed after ExecuteInTransactionAsync succeeds.");
    }

    [Fact]
    public async Task ExecuteInTransactionAsync_RollsBack_WhenWorkThrows()
    {
        var tenantId = $"t-uow-{Guid.NewGuid():N}";

        // Act: seed inside the transaction then throw — must roll back entirely (Inv-4).
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var ctx = fixture.CreateContext(tenantId);
            var uow = new UnitOfWork(ctx);
            await uow.ExecuteInTransactionAsync(async ct =>
            {
                await TestData.SeedOperationAsync(ctx, tenantId, ct: ct);
                throw new InvalidOperationException("boom");
            });
        });

        // Verify NO rows remain for this tenant after the rollback.
        await using var freshCtx = fixture.CreateContext(tenantId);

        var intentExists = await freshCtx.BookingIntents
            .IgnoreQueryFilters()
            .AnyAsync(i => i.TenantId == tenantId);

        var operationExists = await freshCtx.BookingOperations
            .IgnoreQueryFilters()
            .AnyAsync(o => o.TenantId == tenantId);

        Assert.False(intentExists, "BookingIntent must be rolled back when ExecuteInTransactionAsync throws (Inv-4).");
        Assert.False(operationExists, "BookingOperation must be rolled back when ExecuteInTransactionAsync throws (Inv-4).");
    }
}
