using System.Net;
using Alba;
using Cordia.Api.Middleware;
using Microsoft.AspNetCore.Hosting;

namespace Cordia.Tests.IntegrationTests.Observability;

[Trait("Category", "Integration")]
public class CorrelationIntegrationTests
{
    [Fact]
    public async Task NoInboundHeader_MintsCorrelationId()
    {
        await using var host = await AlbaHost.For<Program>(builder =>
            builder.UseEnvironment("Test"));

        var result = await host.Scenario(scenario =>
        {
            scenario.Get.Url("/api/health");
            scenario.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var header = result.Context.Response.Headers[CorrelationMiddleware.CorrelationHeader].ToString();
        Assert.StartsWith("corr_", header);
    }

    [Fact]
    public async Task InboundHeader_IsEchoedUnchanged()
    {
        await using var host = await AlbaHost.For<Program>(builder =>
            builder.UseEnvironment("Test"));

        const string inbound = "corr_test123";

        var result = await host.Scenario(scenario =>
        {
            scenario.Get.Url("/api/health");
            scenario.WithRequestHeader(CorrelationMiddleware.CorrelationHeader, inbound);
            scenario.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var header = result.Context.Response.Headers[CorrelationMiddleware.CorrelationHeader].ToString();
        Assert.Equal(inbound, header);
    }

    [Fact]
    public async Task MalformedInboundHeader_IsRejectedAndFreshlyMinted()
    {
        // A header carrying control chars / injection payload must NOT be echoed (log-injection /
        // response-splitting guard); the middleware mints a clean id instead.
        await using var host = await AlbaHost.For<Program>(builder =>
            builder.UseEnvironment("Test"));

        const string malicious = "corr_evil with spaces";

        var result = await host.Scenario(scenario =>
        {
            scenario.Get.Url("/api/health");
            scenario.WithRequestHeader(CorrelationMiddleware.CorrelationHeader, malicious);
            scenario.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var header = result.Context.Response.Headers[CorrelationMiddleware.CorrelationHeader].ToString();
        Assert.NotEqual(malicious, header);
        Assert.StartsWith("corr_", header);
    }

    [Fact]
    public async Task InboundCausationHeader_IsNotEchoedOnResponse()
    {
        // Causation is internal lineage metadata; it must never be reflected back to the client.
        await using var host = await AlbaHost.For<Program>(builder =>
            builder.UseEnvironment("Test"));

        var result = await host.Scenario(scenario =>
        {
            scenario.Get.Url("/api/health");
            scenario.WithRequestHeader(CorrelationMiddleware.CausationHeader, "cause_test123");
            scenario.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var causation = result.Context.Response.Headers[CorrelationMiddleware.CausationHeader].ToString();
        Assert.True(string.IsNullOrEmpty(causation));
    }
}
