using Cordia.Infrastructure.Extensions;
using Cordia.Infrastructure.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Cordia.Tests.UnitTests.Hardening;

/// <summary>
/// The stub catalogue behind the startup warning (#75, D1): which stubs are in service, which
/// opt-outs are refused, and that the catalogue still describes what the container really registers.
/// </summary>
[Trait("Category", "Unit")]
public class StubPortsTests
{
    private static IConfiguration Config(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void ByDefault_EveryStubWithoutARealImplementation_IsReportedActive()
    {
        var active = StubPorts.Active(Config(new()));

        Assert.Equal(StubPorts.All.Select(p => p.Port), active.Select(p => p.Port));
    }

    /// <summary>The extractor has a real client and its flag defaults off, so it is not listed unless opted in.</summary>
    [Fact]
    public void TheMenuExtractor_IsListedOnlyWhenItsStubIsOptedInto()
    {
        Assert.DoesNotContain(StubPorts.Active(Config(new())), p => p.Port == "IIsolatedMenuExtractor");
        Assert.Contains(
            StubPorts.Active(Config(new() { ["LangGraph:UseStubExtractor"] = "true" })),
            p => p.Port == "IIsolatedMenuExtractor");
    }

    [Fact]
    public void SwitchingOffAStubWithNoRealImplementation_IsRefused()
    {
        var refused = StubPorts.RefusedOptOuts(Config(new() { ["Stubs:BookingProvider"] = "false" }));

        var port = Assert.Single(refused);
        Assert.Equal("IBookingProviderAdapter", port.Port);
    }

    /// <summary>The paired negative: an explicit "true" is the default restated, and refuses nothing.</summary>
    [Fact]
    public void KeepingAStubOnExplicitly_IsNotRefused()
        => Assert.Empty(StubPorts.RefusedOptOuts(Config(new() { ["Stubs:BookingProvider"] = "true" })));

    /// <summary>The extractor can be switched off — it has a real client — so its flag is never refused.</summary>
    [Fact]
    public void SwitchingOffTheMenuExtractorStub_IsNotRefused()
        => Assert.Empty(StubPorts.RefusedOptOuts(Config(new() { ["LangGraph:UseStubExtractor"] = "false" })));

    /// <summary>The notification adapters send nothing; a deployment must be able to find that at startup.</summary>
    [Fact]
    public void TheNotificationAdapters_AreListedAsStubs()
    {
        var active = StubPorts.Active(Config(new())).Select(p => p.Implementation).ToList();

        Assert.Contains(nameof(SmsNotificationAdapter), active);
        Assert.Contains(nameof(EmailNotificationAdapter), active);
        Assert.Contains(nameof(ChannelCallbackAdapter), active);
    }

    [Fact]
    public void Describe_NamesPortImplementationAndConsequence()
    {
        var line = StubPorts.Describe([StubPorts.All[0]]);

        Assert.Contains("IBookingProviderAdapter", line);
        Assert.Contains(nameof(StubBookingProviderAdapter), line);
        Assert.Contains("never sent to a provider", line);
    }

    /// <summary>
    /// Drift guard. If a real adapter replaces a stub, the warning must stop naming it — and if a new
    /// stub is registered under a listed port, the warning must not name the old one. Checked against
    /// the registrations <c>AddInfrastructure</c> actually makes.
    /// </summary>
    [Fact]
    public void TheCatalogue_MatchesWhatTheContainerRegisters()
    {
        var configuration = Config(new() { ["LangGraph:UseStubExtractor"] = "true" });
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddInfrastructure(configuration, new DevelopmentEnvironment());

        foreach (var port in StubPorts.All.Append(StubPorts.MenuExtractor))
        {
            // Contains, not Single: the notification port has one registration per channel.
            Assert.Contains(services, d => d.ServiceType.Name == port.Port
                && d.ImplementationType?.Name == port.Implementation);
        }
    }

    private sealed class DevelopmentEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Cordia.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
