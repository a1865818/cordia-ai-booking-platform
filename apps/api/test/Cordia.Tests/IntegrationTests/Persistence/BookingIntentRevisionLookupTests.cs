#pragma warning disable xUnit1051
using Cordia.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class BookingIntentRevisionLookupTests(PostgresFixture fixture)
{
    // (1) GetRevisionAsync returns the seeded revision with correct fields
    [Fact]
    public async Task GetRevisionAsync_SeededRevision_ReturnsIt()
    {
        var tenantId = $"t3-bir-{Guid.NewGuid():N}";
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        // SeedOperationAsync creates intent + revision (rev=1, payload_hash="h", CanonicalPayload="{}")
        // Load the operation to get intentId
        string intentId;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var op = await ctx.BookingOperations
                .AsNoTracking()
                .SingleAsync(o => o.OperationId == operationId);
            intentId = op.IntentId;
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var repo = new BookingIntentRepository(readCtx);
        var revision = await repo.GetRevisionAsync(tenantId, intentId, 1);

        Assert.NotNull(revision);
        Assert.Equal(tenantId, revision.TenantId);
        Assert.Equal(intentId, revision.IntentId);
        Assert.Equal(1, revision.Revision);
        Assert.Equal("h", revision.PayloadHash);
        // The point of this test is that the lookup returns the row it seeded — not the exact
        // shape of the shared fixture's payload, which is now a realistic one carrying a guest
        // (an empty payload has no notification destination, so it silently turned every
        // notification test into a test of the no-destination branch).
        Assert.Contains("\"operation\"", revision.CanonicalPayload);
    }

    // (2) GetRevisionAsync returns null for an intentId that does not exist
    [Fact]
    public async Task GetRevisionAsync_MissingIntent_ReturnsNull()
    {
        var tenantId = $"t3-bir-{Guid.NewGuid():N}";

        await using var readCtx = fixture.CreateContext(tenantId);
        var repo = new BookingIntentRepository(readCtx);
        var revision = await repo.GetRevisionAsync(tenantId, "int-doesnotexist", 1);

        Assert.Null(revision);
    }

    // (3) GetRevisionAsync returns null for a wrong revision number
    [Fact]
    public async Task GetRevisionAsync_WrongRevisionNumber_ReturnsNull()
    {
        var tenantId = $"t3-bir-{Guid.NewGuid():N}";
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        string intentId;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var op = await ctx.BookingOperations
                .AsNoTracking()
                .SingleAsync(o => o.OperationId == operationId);
            intentId = op.IntentId;
        }

        // Revision 2 was never seeded (only revision 1 exists)
        await using var readCtx = fixture.CreateContext(tenantId);
        var repo = new BookingIntentRepository(readCtx);
        var revision = await repo.GetRevisionAsync(tenantId, intentId, 2);

        Assert.Null(revision);
    }
}
