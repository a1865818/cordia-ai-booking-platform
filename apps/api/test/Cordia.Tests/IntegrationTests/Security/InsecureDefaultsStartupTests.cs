using Alba;
using Cordia.Tests.IntegrationTests.Acceptance;
using Microsoft.AspNetCore.Hosting;

namespace Cordia.Tests.IntegrationTests.Security;

/// <summary>
/// The host refuses to start on committed credentials outside Development (#73). Proven against
/// <c>Program.cs</c> itself, because a detector nobody calls protects nothing.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class InsecureDefaultsStartupTests
{
    [Fact]
    public async Task AStagingHost_OnTheCommittedDatabasePassword_RefusesToStart_NamingTheSetting()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var host = await AlbaHost.For<Program>(builder =>
            {
                builder.UseEnvironment("Staging");
                // The test connection string carries the committed password.
                builder.UseSetting("ConnectionStrings:CordiaDBConnection", AcceptanceApiFixture.TestConnStr);
            });
        });

        var refusal = FindRefusal(ex);
        Assert.NotNull(refusal);
        Assert.Contains("ConnectionStrings:CordiaDBConnection", refusal!.Message);
        Assert.DoesNotContain("localdev", refusal.Message);
    }

    private static InvalidOperationException? FindRefusal(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is InvalidOperationException ioe && ioe.Message.StartsWith("Refusing to start", StringComparison.Ordinal))
                return ioe;
            if (ex is AggregateException agg)
                return agg.InnerExceptions.Select(FindRefusal).FirstOrDefault(r => r is not null);
        }
        return null;
    }
}
