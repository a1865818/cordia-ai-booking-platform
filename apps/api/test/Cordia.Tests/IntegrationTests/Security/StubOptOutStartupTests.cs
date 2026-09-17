using Alba;
using Cordia.Tests.IntegrationTests.Acceptance;
using Microsoft.AspNetCore.Hosting;

namespace Cordia.Tests.IntegrationTests.Security;

/// <summary>
/// Switching off a stub that has no real replacement is refused at startup (#75, D1), through
/// <c>Program.cs</c> itself — silently keeping the stub would leave the operator believing it was off.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class StubOptOutStartupTests
{
    [Fact]
    public async Task AHost_ThatSwitchesOffTheBookingProviderStub_RefusesToStart_NamingTheFlag()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var host = await AlbaHost.For<Program>(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("ConnectionStrings:CordiaDBConnection", AcceptanceApiFixture.TestConnStr);
                builder.UseSetting("Stubs:BookingProvider", "false");
            });
        });

        var refusal = Flatten(ex).FirstOrDefault(e =>
            e is InvalidOperationException && e.Message.Contains("stubs were switched off", StringComparison.Ordinal));
        Assert.NotNull(refusal);
        Assert.Contains("Stubs:BookingProvider", refusal!.Message);
    }

    private static IEnumerable<Exception> Flatten(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            yield return ex;
            if (ex is AggregateException agg)
                foreach (var inner in agg.InnerExceptions.SelectMany(Flatten))
                    yield return inner;
        }
    }
}
