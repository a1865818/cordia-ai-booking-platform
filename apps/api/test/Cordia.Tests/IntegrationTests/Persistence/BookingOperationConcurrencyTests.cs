#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.EntityFramework.Concurrency;
using Cordia.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class BookingOperationConcurrencyTests(PostgresFixture fixture)
{
    private static async Task SeedPrerequisitesAsync(
        Cordia.Infrastructure.EntityFramework.ApplicationDbContext ctx,
        string tenantId,
        string intentId)
    {
        var intent = new BookingIntent
        {
            TenantId = tenantId,
            IntentId = intentId,
            OperationType = OperationTypeEnum.Create,
        };
        var revision = new BookingIntentRevision
        {
            TenantId = tenantId,
            IntentId = intentId,
            Revision = 1,
            CanonicalPayload = "{}",
            PayloadHash = "hash-seed",
            SchemaVersion = "1.0",
            TenantPolicyVersion = "1.0",
            Intent = intent,
        };

        ctx.BookingIntents.Add(intent);
        ctx.BookingIntentRevisions.Add(revision);
        await ctx.SaveChangesAsync();
    }

    // ── Version-token concurrency: exactly one of two concurrent transitions wins ──
    [Fact]
    public async Task TryTransitionStatus_ExactlyOneWins()
    {
        var tenantId = $"t1-op-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";

        // Seed intent + revision prerequisites
        await using (var seedCtx = fixture.CreateContext(tenantId))
            await SeedPrerequisitesAsync(seedCtx, tenantId, intentId);

        // Add the booking operation
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.BookingOperations.Add(new BookingOperation
            {
                TenantId = tenantId,
                OperationId = operationId,
                IntentId = intentId,
                IntentRevision = 1,
                OperationType = OperationTypeEnum.Create,
                Status = OperationStatus.Accepted,
                Version = 0,
            });
            await ctx.SaveChangesAsync();
        }

        // Two separate contexts both try to transition from version 0 to Validating
        await using var ctx1 = fixture.CreateContext(tenantId);
        await using var ctx2 = fixture.CreateContext(tenantId);

        var rows1 = await VersionTokenUpdate.UpdateAsync<BookingOperation>(
            ctx1,
            expectedVersion: 0,
            e => e.TenantId == tenantId && e.OperationId == operationId,
            s => s
                .SetProperty(x => x.Status, OperationStatus.Validating)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow));

        var rows2 = await VersionTokenUpdate.UpdateAsync<BookingOperation>(
            ctx2,
            expectedVersion: 0,
            e => e.TenantId == tenantId && e.OperationId == operationId,
            s => s
                .SetProperty(x => x.Status, OperationStatus.Validating)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow));

        Assert.Equal(1, rows1 + rows2); // exactly one wins
        Assert.True(VersionTokenUpdate.ShouldReload(rows1) || VersionTokenUpdate.ShouldReload(rows2));

        // Verify persisted state: Status=Validating, Version=1
        await using var verifyCtx = fixture.CreateContext(tenantId);
        var reloaded = await verifyCtx.BookingOperations
            .AsNoTracking()
            .SingleAsync(o => o.OperationId == operationId);

        Assert.Equal(OperationStatus.Validating, reloaded.Status);
        Assert.Equal(1, reloaded.Version);
    }

    // ── Status guard (§6.2): a transition with the WRONG expected status returns 0 even when
    //    the version matches, and does not mutate the row; the correct expected status wins. ──
    [Fact]
    public async Task TryTransitionStatus_WrongExpectedStatus_ReturnsZero_AndDoesNotMutate()
    {
        var tenantId = $"t1-opg-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";

        await using (var seedCtx = fixture.CreateContext(tenantId))
            await SeedPrerequisitesAsync(seedCtx, tenantId, intentId);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.BookingOperations.Add(new BookingOperation
            {
                TenantId = tenantId,
                OperationId = operationId,
                IntentId = intentId,
                IntentRevision = 1,
                OperationType = OperationTypeEnum.Create,
                Status = OperationStatus.Accepted,
                Version = 0,
            });
            await ctx.SaveChangesAsync();
        }

        // Wrong expected status (Validating) but matching version (0) → must NOT transition.
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var repo = new BookingOperationRepository(ctx);
            var rows = await repo.TryTransitionStatusAsync(
                tenantId, operationId, expectedVersion: 0,
                expectedStatus: OperationStatus.Validating, next: OperationStatus.Booked);
            Assert.Equal(0, rows);
        }

        await using (var verifyCtx = fixture.CreateContext(tenantId))
        {
            var unchanged = await verifyCtx.BookingOperations.AsNoTracking()
                .SingleAsync(o => o.OperationId == operationId);
            Assert.Equal(OperationStatus.Accepted, unchanged.Status); // untouched
            Assert.Equal(0, unchanged.Version);
        }

        // Correct expected status (Accepted) + version 0 → transitions.
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var repo = new BookingOperationRepository(ctx);
            var rows = await repo.TryTransitionStatusAsync(
                tenantId, operationId, expectedVersion: 0,
                expectedStatus: OperationStatus.Accepted, next: OperationStatus.Validating);
            Assert.Equal(1, rows);
        }

        await using (var verifyCtx = fixture.CreateContext(tenantId))
        {
            var moved = await verifyCtx.BookingOperations.AsNoTracking()
                .SingleAsync(o => o.OperationId == operationId);
            Assert.Equal(OperationStatus.Validating, moved.Status);
            Assert.Equal(1, moved.Version);
        }
    }
}
