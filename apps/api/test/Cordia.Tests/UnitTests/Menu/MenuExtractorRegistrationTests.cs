using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Extensions;
using Cordia.Infrastructure.Menu;
using Cordia.Infrastructure.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Cordia.Tests.UnitTests.Menu;

/// <summary>
/// Pins the D-E gating decision: which <see cref="IIsolatedMenuExtractor"/> a container ends up
/// with is a function of <c>LangGraph:UseStubExtractor</c> and nothing else — no environment
/// sniffing, no "the stub is the default".
///
/// <para>The default matters more than either branch. A missing key must yield the REAL client, so
/// a deployment that simply never heard of this setting cannot silently serve owners fabricated
/// sample items while reporting a successful extraction (Inv-16).</para>
/// </summary>
[Trait("Category", "Unit")]
public class MenuExtractorRegistrationTests
{
    [Fact]
    public void UseStubExtractorTrue_ResolvesTheStub()
    {
        using var provider = BuildProvider(new()
        {
            ["LangGraph:Host"] = "http://localhost:2024",
            ["LangGraph:UseStubExtractor"] = "true",
        });

        Assert.IsType<StubIsolatedMenuExtractor>(provider.GetRequiredService<IIsolatedMenuExtractor>());
    }

    [Fact]
    public void UseStubExtractorFalse_ResolvesTheRealClient()
    {
        using var provider = BuildProvider(new()
        {
            ["LangGraph:Host"] = "http://localhost:2024",
            ["LangGraph:UseStubExtractor"] = "false",
        });

        Assert.IsType<LangGraphMenuExtractorClient>(provider.GetRequiredService<IIsolatedMenuExtractor>());
    }

    [Fact]
    public void UseStubExtractorAbsent_ResolvesTheRealClient()
    {
        using var provider = BuildProvider(new() { ["LangGraph:Host"] = "http://localhost:2024" });

        Assert.IsType<LangGraphMenuExtractorClient>(provider.GetRequiredService<IIsolatedMenuExtractor>());
    }

    [Fact]
    public void ArtifactTextResolver_IsRegistered_ForBothBranches()
    {
        // #84 swaps the implementation behind this port; nothing above it moves. It is registered
        // unconditionally so that stays true whichever extractor is selected.
        using var stubbed = BuildProvider(new()
        {
            ["LangGraph:Host"] = "http://localhost:2024",
            ["LangGraph:UseStubExtractor"] = "true",
        });
        using var real = BuildProvider(new() { ["LangGraph:Host"] = "http://localhost:2024" });

        Assert.IsType<InlineMenuArtifactTextResolver>(stubbed.GetRequiredService<IMenuArtifactTextResolver>());
        Assert.IsType<InlineMenuArtifactTextResolver>(real.GetRequiredService<IMenuArtifactTextResolver>());
    }

    [Fact]
    public void Options_BindTheConfiguredValues_AndDefaultTheRest()
    {
        using var provider = BuildProvider(new() { ["LangGraph:Host"] = "http://langgraph.internal:2024" });

        var options = provider.GetRequiredService<IOptions<LangGraphOptions>>().Value;

        Assert.Equal("http://langgraph.internal:2024", options.Host);
        // Mirrors EXTRACT_PATH in apps/langgraph/cordia_agent/menu/http_app.py.
        Assert.Equal("/internal/menu/extract", options.ExtractPath);
        // Deliberately chosen (D-F corollary), not HttpClient's inherited 100s.
        Assert.Equal(120, options.ExtractTimeoutSeconds);
        Assert.False(options.UseStubExtractor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost:2024")]        // no scheme
    [InlineData("ftp://localhost:2024")]  // not an HTTP scheme
    [InlineData("/internal/menu")]        // relative
    public void HostValidation_RejectsAnUnusableHost_ForTheRealClient(string host)
    {
        var options = new LangGraphOptions { Host = host };

        Assert.False(options.HasUsableHost);
    }

    [Theory]
    [InlineData("http://localhost:2024")]
    [InlineData("https://langgraph.internal")]
    public void HostValidation_AcceptsAnAbsoluteHttpHost(string host)
    {
        Assert.True(new LangGraphOptions { Host = host }.HasUsableHost);
    }

    [Theory]
    [InlineData(null)]                    // Host absent entirely
    [InlineData("localhost:2024")]        // no scheme
    [InlineData("ftp://localhost:2024")]  // not an HTTP scheme
    public async Task ValidateOnStart_RefusesToStart_WhenTheRealClientHasNoUsableHost(string? host)
    {
        // The pure HasUsableHost tests above only prove the predicate. This proves the WIRING —
        // that the validator is actually attached with ValidateOnStart and fires at StartAsync
        // rather than on the first ingestion, hours later, as a mystery ExtractFailed catalog.
        using var host_ = BuildHost(host, useStub: null);

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host_.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("LangGraph:Host", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateOnStart_RefusesToStart_OnANonPositiveTimeout()
    {
        using var host = BuildHost("http://localhost:2024", useStub: null, timeoutSeconds: "0");

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("ExtractTimeoutSeconds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateOnStart_AllowsAHostlessConfiguration_WhenTheStubIsSelected()
    {
        // The stub never dials out, so an offline configuration with no Host must still boot.
        using var host = BuildHost(host: null, useStub: true);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private static IHost BuildHost(string? host, bool? useStub, string timeoutSeconds = "120")
    {
        var settings = new Dictionary<string, string?>
        {
            ["LangGraph:ExtractTimeoutSeconds"] = timeoutSeconds,
        };
        if (host is not null)
            settings["LangGraph:Host"] = host;
        if (useStub is not null)
            settings["LangGraph:UseStubExtractor"] = useStub.Value ? "true" : "false";

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IConfiguration>(builder.Configuration);
        builder.Services.AddInfrastructure(builder.Configuration);

        return builder.Build();
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        // The real host registers IConfiguration itself; AddInfrastructure only takes it as an
        // argument. `BindConfiguration` and ConfigurationSecretStore both resolve it from DI.
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructure(configuration);

        return services.BuildServiceProvider();
    }
}
