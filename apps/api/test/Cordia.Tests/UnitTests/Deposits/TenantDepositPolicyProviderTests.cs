using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Providers;
using Moq;

namespace Cordia.Tests.UnitTests.Deposits;

/// <summary>Unit tests for the tenant-row <see cref="IDepositPolicy"/> (G2).</summary>
[Trait("Category", "Unit")]
public class TenantDepositPolicyProviderTests
{
    private readonly Mock<ITenantDepositPolicyRepository> _repo = new();

    private TenantDepositPolicyProvider Create() => new(_repo.Object);

    [Fact]
    public async Task GetRequirement_MissingRow_ReturnsNone()
    {
        _repo.Setup(r => r.GetAsync("t-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((TenantDepositPolicy?)null);

        var req = await Create().GetRequirementAsync("t-1", "op-1", CancellationToken.None);

        Assert.False(req.Required);
        Assert.Equal(0, req.AmountMinor);
    }

    [Fact]
    public async Task GetRequirement_RequireFalse_ReturnsNone()
    {
        _repo.Setup(r => r.GetAsync("t-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantDepositPolicy
            {
                TenantId = "t-1",
                Require = false,
                AmountMinor = 5000,
                Currency = "AUD",
            });

        var req = await Create().GetRequirementAsync("t-1", "op-1", CancellationToken.None);

        Assert.False(req.Required);
        Assert.Equal(0, req.AmountMinor);
    }

    [Fact]
    public async Task GetRequirement_RequireTrue_UsesStoredAmountMinor()
    {
        _repo.Setup(r => r.GetAsync("t-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantDepositPolicy
            {
                TenantId = "t-1",
                Require = true,
                AmountMinor = 3500,
                Currency = "AUD",
            });

        var req = await Create().GetRequirementAsync("t-1", "op-1", CancellationToken.None);

        Assert.True(req.Required);
        Assert.Equal(3500, req.AmountMinor);
        Assert.Equal("AUD", req.Currency);
    }

    [Fact]
    public async Task GetRequirement_RequireTrue_NoAmountMinor_DefaultsTo2000Aud()
    {
        _repo.Setup(r => r.GetAsync("t-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantDepositPolicy
            {
                TenantId = "t-1",
                Require = true,
                AmountMinor = null,
                Currency = null,
            });

        var req = await Create().GetRequirementAsync("t-1", "op-1", CancellationToken.None);

        Assert.True(req.Required);
        Assert.Equal(TenantDepositPolicyProvider.DefaultAmountMinor, req.AmountMinor);
        Assert.Equal(TenantDepositPolicyProvider.DefaultCurrency, req.Currency);
    }
}
