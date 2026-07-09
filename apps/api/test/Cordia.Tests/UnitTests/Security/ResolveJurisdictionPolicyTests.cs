using Cordia.Application.Handlers.Security.ResolveJurisdictionPolicy;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using Cordia.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Moq;

namespace Cordia.Tests.UnitTests.Security;

[Trait("Category", "Unit")]
public class ResolveJurisdictionPolicyTests
{
    [Theory]
    [InlineData("voice", TenantChannel.Voice)]
    [InlineData("sms", TenantChannel.Messaging)]
    [InlineData("messaging", TenantChannel.Messaging)]
    [InlineData("web", TenantChannel.Web)]
    [InlineData("email", TenantChannel.Email)]
    public async Task Handler_MapsWireChannelOntoTenantChannel(string wire, TenantChannel expected)
    {
        var provider = new Mock<IJurisdictionPolicyProvider>();
        provider.Setup(p => p.Resolve("t1", expected))
            .Returns(new JurisdictionPolicy("jp_x", "v1", true, null));

        var result = await new ResolveJurisdictionPolicyQueryHandler(provider.Object)
            .Handle(new ResolveJurisdictionPolicyQuery("t1", wire), CancellationToken.None);

        Assert.Equal("jp_x", result.JurisdictionPolicyId);
        provider.VerifyAll();
    }

    [Theory]
    [InlineData("voice", true)]
    [InlineData("carrier-pigeon", false)]
    [InlineData("", false)]
    public void Validator_EnforcesClosedChannelSet(string channel, bool expectedValid)
    {
        var result = new ResolveJurisdictionPolicyValidator()
            .Validate(new ResolveJurisdictionPolicyQuery("t1", channel));
        Assert.Equal(expectedValid, result.IsValid);
    }

    [Fact]
    public void Provider_FailsClosed_WhenNothingIsConfigured()
    {
        var config = new ConfigurationBuilder().Build();
        var policy = new JurisdictionPolicyProvider(config).Resolve("t1", TenantChannel.Voice);

        Assert.True(policy.NoticeRequired, "absent configuration must require the notice (fail-closed)");
        Assert.Equal("jp_default", policy.JurisdictionPolicyId);
        Assert.Equal("default:2026-01", policy.ConsentPolicyVersion);
        Assert.Null(policy.NoticeText);
    }

    [Fact]
    public void Provider_UsesChannelOverridesWhenConfigured()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Consent:DefaultJurisdictionPolicyId"] = "jp_au",
                ["Consent:DefaultConsentPolicyVersion"] = "au:2026-01",
                ["Consent:Channels:Web:NoticeRequired"] = "false",
                ["Consent:Channels:Voice:NoticeText"] = "This call is recorded.",
            })
            .Build();
        var provider = new JurisdictionPolicyProvider(config);

        var web = provider.Resolve("t1", TenantChannel.Web);
        Assert.False(web.NoticeRequired);
        Assert.Equal("jp_au", web.JurisdictionPolicyId);

        var voice = provider.Resolve("t1", TenantChannel.Voice);
        Assert.True(voice.NoticeRequired);
        Assert.Equal("This call is recorded.", voice.NoticeText);
        Assert.Equal("au:2026-01", voice.ConsentPolicyVersion);
    }
}
