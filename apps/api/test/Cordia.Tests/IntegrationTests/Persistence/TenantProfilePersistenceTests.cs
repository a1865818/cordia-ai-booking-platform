#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Infrastructure.Repositories;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class TenantProfilePersistenceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Upsert_InsertsThenUpdatesHoursAndBusinessType()
    {
        var tenantId = $"t-profile-{Guid.NewGuid():N}";
        const string hours = """[{"d":"Monday","open":"09:00","close":"18:00","on":true}]""";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await new TenantProfileRepository(ctx).UpsertAsync(new TenantProfile
            {
                TenantId = tenantId,
                BusinessType = "salon",
                WaitlistAutoBook = false,
                HoursJson = hours,
            });
        }

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var loaded = await new TenantProfileRepository(ctx).GetAsync(tenantId);
            Assert.NotNull(loaded);
            Assert.Equal("salon", loaded.BusinessType);
            Assert.False(loaded.WaitlistAutoBook);
            Assert.Contains("Monday", loaded.HoursJson);

            await new TenantProfileRepository(ctx).UpsertAsync(new TenantProfile
            {
                TenantId = tenantId,
                BusinessType = "restaurant",
                WaitlistAutoBook = true,
                HoursJson = hours,
            });
        }

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var updated = await new TenantProfileRepository(ctx).GetAsync(tenantId);
            Assert.NotNull(updated);
            Assert.Equal("restaurant", updated.BusinessType);
            Assert.True(updated.WaitlistAutoBook);
        }
    }
}
