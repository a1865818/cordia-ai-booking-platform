using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Extensions;
using Cordia.Infrastructure.Menu;
using Cordia.Infrastructure.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
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
    /// <summary>Where the shared secret lives; mirrors the client's own constant.</summary>
    private const string SecretKey = "LangGraphMenuExtractionKey";

    // `Environments.Development` / `.Production` are static readonly fields, not consts, so they
    // cannot appear in an [InlineData] attribute or a default parameter value. Same strings.
    private const string DevelopmentEnvironment = "Development";
    private const string ProductionEnvironment = "Production";

    /// <summary>
    /// A syntactically valid secret for the tests that are not about the secret. Never a real one,
    /// and printable ASCII on purpose — that is the contract both sides enforce.
    /// </summary>
    private const string DevSecret = "TEST-ONLY-not-a-real-menu-extraction-key";

    /// <summary>
    /// A host that is absolute, http, path-free and NOT loopback — i.e. one that clears every
    /// deployment gate. `.invalid` is reserved by RFC 2606, so a regression that ever dialled it
    /// fails loudly instead of quietly reaching whatever is on localhost.
    /// </summary>
    private const string DeployableHost = "http://langgraph.invalid:8000";

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
        // Development, so the environment-dependent gates below are out of the picture and this
        // test still pins exactly one rule.
        using var host_ = BuildHost(host, useStub: null, environmentName: DevelopmentEnvironment);

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host_.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("LangGraph:Host", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateOnStart_RefusesToStart_OnANonPositiveTimeout()
    {
        using var host = BuildHost(
            "http://localhost:2024",
            useStub: null,
            timeoutSeconds: "0",
            environmentName: DevelopmentEnvironment);

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
        using var host = BuildHost(
            "http://localhost:2024",
            useStub: null,
            extractPath: extractPath,
            environmentName: DevelopmentEnvironment);

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("ExtractPath", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/internal/menu/extract")]
    [InlineData("internal/menu/extract")]
    public async Task ValidateOnStart_AcceptsARelativeExtractPath(string extractPath)
    {
        using var host = BuildHost(
            "http://localhost:2024",
            useStub: null,
            extractPath: extractPath,
            environmentName: DevelopmentEnvironment);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // -------------------------------------------------------------------------------------------
    // Deployment gates (issue #94). Each one converts a misconfiguration that used to boot green
    // and fail on every ingestion into a refusal to start.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A DEPLOYED API whose <c>LangGraph:Host</c> is a loopback address is dialling itself.
    ///
    /// <para>This was live: <c>appsettings.json</c> shipped <c>http://localhost:2024</c>, no deploy
    /// config overrode it, and <see cref="LangGraphOptions.HasUsableHost"/> could not catch it
    /// because that string IS an absolute http URI. Startup logged nothing about the resolved host,
    /// so the first symptom was every menu ingestion settling <c>ExtractFailed</c> with a generic
    /// reason.</para>
    ///
    /// <para><c>0.0.0.0</c> and <c>[::]</c> are in the set because as an outbound TARGET the
    /// unspecified address means the local host, even though neither <see cref="Uri.IsLoopback"/>
    /// nor <c>IPAddress.IsLoopback</c> calls it loopback.</para>
    /// </summary>
    [Theory]
    [InlineData("http://localhost:2024")]
    [InlineData("https://localhost")]
    [InlineData("http://127.0.0.1:2024")]
    [InlineData("http://127.0.0.2:2024")]   // all of 127.0.0.0/8, not just .1
    [InlineData("http://[::1]:2024")]
    [InlineData("http://0.0.0.0:8000")]
    [InlineData("http://[::]:8000")]
    public async Task ValidateOnStart_RefusesToStart_OnALoopbackHost_OutsideDevelopment(string host)
    {
        using var host_ = BuildHost(host, useStub: null, environmentName: ProductionEnvironment);

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host_.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("loopback", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same configuration in Development starts, because there a loopback host is the CORRECT
    /// one — `langgraph dev` is on :2024 on this machine. The gate must not make the local loop
    /// harder than the deployment.
    /// </summary>
    [Fact]
    public async Task ValidateOnStart_AllowsALoopbackHost_InDevelopment()
    {
        using var host = BuildHost(
            "http://localhost:2024",
            useStub: null,
            environmentName: DevelopmentEnvironment,
            secret: null);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The stub never dials out, so an offline loopback configuration boots anywhere.</summary>
    [Fact]
    public async Task ValidateOnStart_AllowsALoopbackHost_WhenTheStubIsSelected()
    {
        using var host = BuildHost(
            "http://localhost:2024",
            useStub: true,
            environmentName: ProductionEnvironment,
            secret: null);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A path on the host is DISCARDED, not prefixed: <c>HttpClient</c> resolves the root-relative
    /// <c>ExtractPath</c> against <c>BaseAddress</c> per RFC 3986, which replaces the base path
    /// wholesale. <c>https://gw.internal/langgraph</c> therefore POSTs to
    /// <c>https://gw.internal/internal/menu/extract</c> and comes back 404 — surfaced to the owner
    /// as "the agent returned HTTP 404". Addressing the agent through a reverse-proxy or Front Door
    /// route is exactly how a deployment would do it.
    ///
    /// <para>Environment-independent: the path is just as silently lost in Development.</para>
    /// </summary>
    [Theory]
    [InlineData("https://gw.internal/langgraph")]
    [InlineData("https://gw.internal/langgraph/")]
    [InlineData("http://langgraph.invalid:8000/agent/v1")]
    [InlineData("http://langgraph.invalid:8000/?trace=1")]
    [InlineData("http://langgraph.invalid:8000/#frag")]
    public async Task ValidateOnStart_RefusesToStart_OnAHostCarryingAPath(string host)
    {
        using var host_ = BuildHost(host, useStub: null, environmentName: ProductionEnvironment);

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host_.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("scheme and authority only", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateOnStart_RefusesToStart_OnAHostCarryingAPath_EvenInDevelopment()
    {
        using var host = BuildHost(
            "https://gw.internal/langgraph",
            useStub: null,
            environmentName: DevelopmentEnvironment);

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("scheme and authority only", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A bare authority — with or without a trailing slash or port — is what is wanted.</summary>
    [Theory]
    [InlineData("http://langgraph.invalid:8000")]
    [InlineData("http://langgraph.invalid:8000/")]
    [InlineData("https://langgraph.invalid")]
    public async Task ValidateOnStart_AcceptsAnAuthorityOnlyHost(string host)
    {
        using var host_ = BuildHost(host, useStub: null, environmentName: ProductionEnvironment);

        await host_.StartAsync(TestContext.Current.CancellationToken);
        await host_.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// D-G, fail closed. The shared secret was configured in no appsettings file and no deploy
    /// config, so the deployed client sent no header — and the route fell open, leaving the
    /// extraction lane unauthenticated on a port whose <c>/openapi.json</c> advertises it. Now the
    /// route refuses to serve anonymously without an explicit opt-out, so an API with no secret
    /// would 401 on every ingestion instead. Refusing to start says so at the deploy, not hours
    /// later.
    /// </summary>
    [Theory]
    [InlineData(null)]     // key absent entirely
    [InlineData("")]       // present and empty
    [InlineData("   ")]    // whitespace: the client sends nothing, the route expects something
    public async Task ValidateOnStart_RefusesToStart_WithoutTheSharedSecret_OutsideDevelopment(
        string? secret)
    {
        using var host = BuildHost(
            DeployableHost, useStub: null, environmentName: ProductionEnvironment, secret: secret);

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(SecretKey, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A NON-ASCII secret is a permanent 401 that neither side can diagnose: Starlette decodes
    /// header values as latin-1 while the agent reads its environment as UTF-8, and the two agree
    /// only on ASCII. Control characters go with them, because the client adds the header with
    /// <c>TryAddWithoutValidation</c> — a CR/LF in the secret would be request-header injection
    /// rather than a failed comparison.
    ///
    /// <para>Refused in Development too. Unlike the missing-secret rule this is not a posture
    /// difference: a secret that cannot survive the wire is wrong everywhere, and a developer who
    /// hits it locally is the cheapest person to find it.</para>
    /// </summary>
    [Theory]
    [InlineData("clé-secrète")]                 // accented Latin
    [InlineData("秘密")]                          // CJK
    [InlineData("key-with-a-\u0000-nul")]
    [InlineData("key-with-a\r\nInjected: header")]
    public async Task ValidateOnStart_RefusesToStart_OnANonAsciiOrControlSecret(string secret)
    {
        foreach (var environmentName in new[] { ProductionEnvironment, DevelopmentEnvironment })
        {
            using var host = BuildHost(
                DeployableHost, useStub: null, environmentName: environmentName, secret: secret);

            var ex = await Assert.ThrowsAsync<OptionsValidationException>(
                () => host.StartAsync(TestContext.Current.CancellationToken));

            Assert.Contains("printable", ex.Message, StringComparison.Ordinal);
            // The SECRET ITSELF must never appear in a startup diagnostic (Inv-12).
            Assert.DoesNotContain(secret, ex.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The whole point of the gates: a correctly configured deployment starts. Non-loopback,
    /// path-free host, printable-ASCII secret, real client, Production.
    /// </summary>
    [Fact]
    public async Task ValidateOnStart_StartsCleanly_OnAFullyConfiguredDeployment()
    {
        using var host = BuildHost(
            DeployableHost, useStub: null, environmentName: ProductionEnvironment);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// And the two configurations the repo actually ships: Development and the test host, both of
    /// which opt into the stub and supply no secret at all.
    /// </summary>
    [Theory]
    [InlineData(DevelopmentEnvironment)]
    [InlineData("Test")]
    public async Task ValidateOnStart_StartsCleanly_OnTheShippedStubConfigurations(string environmentName)
    {
        using var host = BuildHost(
            "http://localhost:2024",
            useStub: true,
            environmentName: environmentName,
            secret: null);

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

    /// <summary>
    /// Builds a real <see cref="IHost"/> so <c>ValidateOnStart</c> genuinely runs at
    /// <c>StartAsync</c>.
    ///
    /// <para><paramref name="environmentName"/> is always explicit. Several of the gates below are
    /// environment-dependent, and <see cref="Host.CreateEmptyApplicationBuilder"/> reads no
    /// environment variables (defaults are disabled), so leaving it implicit would pin whatever
    /// constant that overload happens to use rather than the behaviour under test.</para>
    /// </summary>
    private static IHost BuildHost(
        string? host,
        bool? useStub,
        string timeoutSeconds = "120",
        string? extractPath = null,
        string environmentName = ProductionEnvironment,
        string? secret = DevSecret)
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
        if (secret is not null)
            settings[$"Secrets:{SecretKey}"] = secret;

        var builder = Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings { EnvironmentName = environmentName });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IConfiguration>(builder.Configuration);
        builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

        return builder.Build();
    }

    private static ServiceProvider BuildProvider(
        Dictionary<string, string?> settings, string environmentName = DevelopmentEnvironment)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        // The real host registers IConfiguration itself; AddInfrastructure only takes it as an
        // argument. `BindConfiguration` and ConfigurationSecretStore both resolve it from DI.
        services.AddSingleton<IConfiguration>(configuration);
        // Development by default here: these tests are about WHICH extractor a container resolves,
        // and they configure the loopback host and no shared secret — the dev posture. The
        // deployment gates that refuse exactly that configuration are pinned separately below,
        // through a real host, because only a host runs ValidateOnStart.
        services.AddInfrastructure(configuration, new TestHostEnvironment(environmentName));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Minimal <see cref="IHostEnvironment"/>. A hand-built <see cref="ServiceCollection"/> has no
    /// host and therefore no environment, and <c>AddInfrastructure</c> reads
    /// <c>IsDevelopment()</c> off this to decide whether the deployment gates apply.
    /// </summary>
    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Cordia.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
