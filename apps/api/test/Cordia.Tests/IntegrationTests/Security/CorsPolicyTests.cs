#pragma warning disable xUnit1051
using Alba;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.AspNetCore.Hosting;

namespace Cordia.Tests.IntegrationTests.Security;

/// <summary>
/// Pins the CORS policy (#81).
///
/// <para><b>What was actually wrong.</b> The policy was
/// <c>WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()</c>. That is lower risk than the issue
/// title suggests — origins are an allowlist that is <b>empty</b> by default outside Development,
/// and the policy sets no <c>AllowCredentials</c>, so the wildcards could not be used to ride a
/// session. It was still a wildcard nobody needed.</para>
///
/// <para><b>Why this file has to exist.</b> Narrowing the header list is the one change in this wave
/// that can break the console in a way no other test would notice: a header the client sends and
/// the policy omits fails at browser <i>preflight</i>, which never happens in a .NET test and cannot
/// happen in the web suite either (that suite drives a mocked <c>fetch</c>, and it cannot run below
/// Bun 1.4 locally at all). So the allowed set is asserted here, header by header, against the real
/// pipeline.</para>
///
/// <para>The host is booted with an origin configured because <c>appsettings.Test.json</c> declares
/// no <c>Cors</c> section and therefore inherits the empty production allowlist — a policy that
/// allows nothing would make every assertion below vacuously green.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class CorsPolicyTests : IAsyncLifetime
{
    private const string AllowedOrigin = "https://console.cordia.test";
    private const string ForeignOrigin = "https://not-the-console.example";

    /// <summary>
    /// Every header <c>apps/web/src/api/client.ts</c> sets. Duplicated here on purpose: this test
    /// is the thing that notices when the two lists stop agreeing.
    /// </summary>
    public static readonly TheoryData<string> ConsoleHeaders =
    [
        "Content-Type",
        "X-Actor",
        "X-Actor-Role",
        "X-Correlation-Id",
        "X-Grant-Id",
        "X-Reason-Code",
        "X-Tenant-Context",
        "X-Tenant-Id",
    ];

    private IAlbaHost _host = null!;

    public async ValueTask InitializeAsync() =>
        // The Postgres collection fixture has already created and migrated the schema under
        // TestSchemaLock, so this host does not migrate — it would be a fourth concurrent migrator.
        _host = await AlbaHost.For<Program>(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("ConnectionStrings:CordiaDBConnection", PostgresFixture.TestConnStr);
            builder.UseSetting("DatabaseBootstrap:MigrateOnStartup", "false");
            builder.UseSetting("Cors:AllowedOrigins:0", AllowedOrigin);
        });

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
    }

    // ── The console's own headers must survive the narrowing ───────────────────────

    [Theory]
    [MemberData(nameof(ConsoleHeaders))]
    public async Task Preflight_AllowsEveryHeaderTheConsoleSends(string header)
    {
        var allowed = await PreflightHeaderAsync(
            AllowedOrigin, "POST", header, "Access-Control-Allow-Headers");

        Assert.False(
            string.IsNullOrEmpty(allowed),
            $"CORS preflight refused '{header}', which apps/web/src/api/client.ts sends on real "
            + "requests. The browser would block that call. Add it to corsHeaders in Program.cs.");

        Assert.Contains(header, allowed, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Preflight_AllowsEveryMethodTheConsoleUses(string method)
    {
        var allowed = await PreflightHeaderAsync(
            AllowedOrigin, method, "Content-Type", "Access-Control-Allow-Methods");

        Assert.Contains(method, allowed, StringComparison.OrdinalIgnoreCase);
    }

    // ── …and the wildcards must be gone ─────────────────────────────────────────────

    /// <summary>
    /// The actual #81 assertion. Under <c>AllowAnyHeader()</c> the response echoed whatever was
    /// asked for, so this passed for a header the API has no concept of.
    /// </summary>
    [Fact]
    public async Task Preflight_RefusesAHeaderTheApiDoesNotUse()
    {
        var allowed = await PreflightHeaderAsync(
            AllowedOrigin, "POST", "X-Totally-Made-Up", "Access-Control-Allow-Headers");

        Assert.DoesNotContain("X-Totally-Made-Up", allowed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Provider- and webhook-facing headers are §13-C surface: never sent from a browser. Allowing
    /// them would advertise a capability the console must not have.
    /// </summary>
    [Theory]
    [InlineData("X-Webhook-Secret")]
    [InlineData("X-Provider-Message-Id")]
    public async Task Preflight_RefusesProviderOnlyHeaders(string header)
    {
        var allowed = await PreflightHeaderAsync(
            AllowedOrigin, "POST", header, "Access-Control-Allow-Headers");

        Assert.DoesNotContain(header, allowed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The property that made the original wildcards survivable, asserted so it stays true: origins
    /// are an allowlist, and an unlisted one gets nothing regardless of headers or method.
    /// </summary>
    [Fact]
    public async Task Preflight_FromAnUnlistedOrigin_IsNotAllowed()
    {
        var allowOrigin = await PreflightHeaderAsync(
            ForeignOrigin, "POST", "Content-Type", "Access-Control-Allow-Origin");

        Assert.True(
            string.IsNullOrEmpty(allowOrigin),
            $"An unlisted origin received Access-Control-Allow-Origin: '{allowOrigin}'.");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Issues a real CORS preflight and returns one <c>Access-Control-*</c> response header, or the
    /// empty string when the policy declined to emit it.
    ///
    /// <para>Alba's <c>Scenario</c> has no OPTIONS verb, and preflight is the only place the header
    /// and method allowlists are observable at all — a simple request only ever reveals
    /// <c>Access-Control-Allow-Origin</c>. So this drives the pipeline through the test server's own
    /// <see cref="HttpClient"/> instead.</para>
    ///
    /// <para>The probe route need not exist: a preflight is answered by the CORS middleware before
    /// routing, and only the response headers are under test.</para>
    /// </summary>
    private async Task<string> PreflightHeaderAsync(
        string origin, string requestMethod, string requestHeader, string responseHeader)
    {
        using var client = _host.Server.CreateClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Options, "/api/booking-operations/op-preflight-probe");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", requestMethod);
        request.Headers.Add("Access-Control-Request-Headers", requestHeader);

        using var response = await client.SendAsync(request);

        return response.Headers.TryGetValues(responseHeader, out var values)
            ? string.Join(", ", values)
            : string.Empty;
    }
}
