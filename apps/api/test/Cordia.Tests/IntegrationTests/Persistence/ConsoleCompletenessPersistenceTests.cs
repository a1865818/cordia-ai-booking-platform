#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.Repositories;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class ConsoleCompletenessPersistenceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task StaffResource_ListActive_ExcludesInactive()
    {
        var tenantId = $"t-staff-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var repo = new StaffResourceRepository(ctx);
            await repo.AddAsync(new StaffResource
            {
                TenantId = tenantId,
                ResourceId = "st-active",
                Name = "Alex",
                Role = "stylist",
                Active = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await repo.AddAsync(new StaffResource
            {
                TenantId = tenantId,
                ResourceId = "st-inactive",
                Name = "Blair",
                Active = false,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var listed = await new StaffResourceRepository(ctx).ListActiveAsync(tenantId);
            Assert.Collection(listed, s =>
            {
                Assert.Equal("st-active", s.ResourceId);
                Assert.Equal("Alex", s.Name);
            });
        }
    }

    [Fact]
    public async Task DepositStatus_Refunded_RoundTripsPgEnum()
    {
        var tenantId = $"t-dep-refund-{Guid.NewGuid():N}";
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        var depId = $"dep-{Guid.NewGuid():N}";
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await new DepositSessionRepository(ctx).AddAsync(new DepositSession
            {
                TenantId = tenantId,
                DepositSessionId = depId,
                OperationId = operationId,
                AmountMinor = 1000,
                Currency = "AUD",
                Status = DepositStatus.Refunded,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            });
        }

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var reloaded = await new DepositSessionRepository(ctx).GetAsync(tenantId, depId);
            Assert.NotNull(reloaded);
            Assert.Equal(DepositStatus.Refunded, reloaded.Status);

            var listed = await new DepositSessionRepository(ctx).ListAsync(
                tenantId, DepositStatus.Refunded, from: null, to: null, limit: 10);
            Assert.Single(listed);
            Assert.Equal(depId, listed[0].DepositSessionId);
        }
    }
}
