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
    [InlineData("http://evil.example/collect")]   // absolute ⇒ HttpClient ignores BaseAddress
    [InlineData("https://evil.example/collect")]
    [InlineData("file:///etc/passwd")]
    [InlineData("//evil.example/collect")]        // "relative" to Uri, yet host-replacing on resolve
    [InlineData(@"\\evil.example\collect")]
    [InlineData("")]
    [InlineData("   ")]
    public void ExtractPathValidation_RejectsAnythingThatCanChangeTheHost(string extractPath)
    {
        Assert.False(new LangGraphOptions { ExtractPath = extractPath }.HasRelativeExtractPath);
    }

    [Theory]
    [InlineData("/internal/menu/extract")]
    [InlineData("internal/menu/extract")]
    [InlineData("/internal/menu/extract?trace=1")]
    public void ExtractPathValidation_AcceptsARouteOnTheConfiguredHost(string extractPath)
    {
        Assert.True(new LangGraphOptions { ExtractPath = extractPath }.HasRelativeExtractPath);
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

    /// <summary>
    /// An ABSOLUTE <c>ExtractPath</c> silently defeats the <c>Host</c> validation above.
    ///
    /// <para><see cref="HttpClient"/> ignores <c>BaseAddress</c> whenever the request URI is
    /// absolute, so <c>ExtractPath = http://evil.example/collect</c> dials that host with the resolved
    /// menu text and the <c>X-Cordia-Internal-Key</c> in hand while <c>LangGraph:Host</c> — the value
    /// that IS checked at startup — is never consulted. Triggering it needs config-write access, so
    /// the severity is low; the defect is that a validated setting can be bypassed by an unvalidated
    /// one.</para>
    ///
    /// <para><b>The protocol-relative cases are why the check resolves rather than merely parsing.</b>
    /// <c>Uri.TryCreate("//evil.example/collect", UriKind.Relative, …)</c> returns TRUE — a
    /// network-path reference is relative per RFC 3986 — yet resolving it against
    /// <c>http://localhost:2024</c> yields <c>http://evil.example/collect</c> (measured). An
    /// absolute-URI-only validator would have let both of them straight through.</para>
    /// </summary>
    [Theory]
    [InlineData("http://evil.example/collect")]
    [InlineData("https://evil.example/collect")]
    [InlineData("file:///etc/passwd")]
    [InlineData("//evil.example/collect")]      // relative to Uri, host-REPLACING once resolved
    [InlineData(@"\\evil.example\collect")]     // Uri normalises this to the same network-path form
    [InlineData("")]
    [InlineData("   ")]
    public async Task ValidateOnStart_RefusesToStart_OnAnExtractPathThatCanChangeTheHost(string extractPath)
    {
        using var host = BuildHost("http://localhost:2024", useStub: null, extractPath: extractPath);

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("ExtractPath", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/internal/menu/extract")]
    [InlineData("internal/menu/extract")]
    public async Task ValidateOnStart_AcceptsARelativeExtractPath(string extractPath)
    {
        using var host = BuildHost("http://localhost:2024", useStub: null, extractPath: extractPath);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ValidateOnStart_AllowsAHostlessConfiguration_WhenTheStubIsSelected()
    {
        // The stub never dials out, so an offline configuration with no Host must still boot.
        using var host = BuildHost(host: null, useStub: true);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private static IHost BuildHost(
        string? host, bool? useStub, string timeoutSeconds = "120", string? extractPath = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["LangGraph:ExtractTimeoutSeconds"] = timeoutSeconds,
        };
        if (host is not null)
            settings["LangGraph:Host"] = host;
        if (extractPath is not null)
            settings["LangGraph:ExtractPath"] = extractPath;
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
