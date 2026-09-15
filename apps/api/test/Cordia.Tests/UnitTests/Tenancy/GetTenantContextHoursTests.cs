using Cordia.Application.Handlers.Tenancy.GetTenantContext;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.Tenancy;

/// <summary>
/// The agent could not say when the venue closes, because <c>get_tenant_context</c> never
/// carried the hours. These pin the new <c>hours</c> field on the payload the agent reads.
/// </summary>
[Trait("Category", "Unit")]
public class GetTenantContextHoursTests
{
    private const string TenantId = "t-context-hours";

    private readonly Mock<ITenantProfileRepository> _profiles = new();

    private Task<TenantContextResult> Get()
        => new GetTenantContextQueryHandler(_profiles.Object)
            .Handle(new GetTenantContextQuery(TenantId, "v3"), TestContext.Current.CancellationToken);

    [Fact]
    public async Task StoredHours_AreServedToTheAgent()
    {
        _profiles
            .Setup(p => p.GetAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantProfile
            {
                TenantId = TenantId,
                Timezone = "Australia/Melbourne",
                HoursJson = """
                    [
                        {"d":"Monday","open":"08:00","close":"22:00","on":false},
                        {"d":"Tuesday","open":"08:00","close":"22:00","on":true},
                        {"d":"Wednesday","open":"08:00","close":"22:00","on":true},
                        {"d":"Thursday","open":"08:00","close":"22:00","on":true},
                        {"d":"Friday","open":"08:00","close":"22:00","on":true},
                        {"d":"Saturday","open":"08:00","close":"22:00","on":true},
                        {"d":"Sunday","open":"08:00","close":"22:00","on":true}
                    ]
                    """,
            });

        var result = await Get();

        Assert.Equal(7, result.Hours.Count);
        Assert.Equal("Monday", result.Hours[0].D);
        Assert.False(result.Hours[0].On);
        Assert.Equal("22:00", result.Hours[1].Close);
    }

    [Fact]
    public async Task NoProfileRow_StillServesSevenDays()
    {
        _profiles
            .Setup(p => p.GetAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TenantProfile?)null);

        var result = await Get();

        Assert.Equal(7, result.Hours.Count);
    }
}
