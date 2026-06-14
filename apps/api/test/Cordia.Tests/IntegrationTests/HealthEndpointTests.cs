using System.Net;
using Alba;
using Microsoft.AspNetCore.Hosting;

namespace Cordia.Tests.IntegrationTests;

[Trait("Category", "Integration")]
public class HealthEndpointTests
{
    [Fact]
    public async Task Health_ReturnsOk()
    {
        await using var host = await AlbaHost.For<Program>(builder =>
            builder.UseEnvironment("Test"));

        await host.Scenario(scenario =>
        {
            scenario.Get.Url("/api/health");
            scenario.StatusCodeShouldBe(HttpStatusCode.OK);
            scenario.ContentShouldContain("ok");
        });
    }
}
