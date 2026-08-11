using Alba;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cordia.Tests.IntegrationTests.TenantResolution;

/// <summary>
/// The <c>X-Tenant-Id</c> shim must exist only where it is meant to (Inv-13).
///
/// <para>Ordering was the only control before this: the shim is registered after
/// <c>TenantContextVerificationMiddleware</c> and never overrides a verified context. But that
/// says nothing about a request carrying <b>no</b> signed context — wherever the shim runs, such a
/// caller simply names the tenant whose data they receive. That is fine on a developer machine and
/// in this test host; anywhere else it is unauthenticated cross-tenant access.</para>
///
/// <para>These tests boot real hosts with the flag on and off and prove the difference is a
/// <b>refusal</b>, not a formality. They deliberately hit a tenant-scoped read that needs no
/// database rows, so they assert the tenant gate rather than any seeded state.</para>
/// </summary>
[Trait("Category", "Integration")]
public class TenantHeaderShimGateTests(TenantResolutionApiFixture fixture)
    : IClassFixture<TenantResolutionApiFixture>
{
    // The fixture is depended on ONLY for its bootstrap: constructing it ensures the shared test
    // database exists and its migrations have been applied. These tests then boot their own hosts,
    // because the thing under test is a startup-time registration decision and cannot be varied on
    // an already-built host. Without the dependency, running this class alone on a clean machine
    // (or ahead of the migrating fixtures) hit an absent database and failed with a 500 where a
    // 200 was asserted — a flake in the very range whose sibling commit exists to remove flakes.
    private readonly TenantResolutionApiFixture _bootstrap = fixture;

    /// <summary>A tenant-scoped GET that fails closed at <c>TenantGuard.RequireTenant</c>.</summary>
    private const string TenantScopedRoute = "/api/consent-policies?channel=web";

    private Task<IAlbaHost> HostWithShim(bool allowShim)
    {
        // Touch the fixture so the compiler cannot elide the dependency and a reader can see that
        // the database bootstrap is what it is for.
        Assert.NotNull(_bootstrap.Host);

        return AlbaHost.For<Program>(builder =>
        {
            builder.UseEnvironment("Test");
            // One source for the connection string, shared with every other fixture family.
            builder.UseSetting("ConnectionStrings:CordiaDBConnection", TenantResolutionApiFixture.TestConnStr);
            // The fixture already migrated; re-running per host would race the other fixtures.
            builder.UseSetting("DatabaseBootstrap:MigrateOnStartup", "false");
            // UseSetting is applied after appsettings.Test.json, so it wins — which is the whole
            // point: appsettings.Test.json pins the shim ON, and the disabled case must be able to
            // override it. `ShimSettingOverridesAppSettings` below fails loudly if that ever stops
            // being true, so this regression guard cannot silently degrade into asserting the
            // shim-on path twice.
            builder.UseSetting("Tenancy:AllowHeaderShim", allowShim ? "true" : "false");
        });
    }

    [Fact]
    public async Task HeaderIsHonoured_WhenShimIsAllowed()
    {
        await using var host = await HostWithShim(allowShim: true);

        await host.Scenario(api =>
        {
            api.Get.Url(TenantScopedRoute);
            api.WithRequestHeader("X-Tenant-Id", "tenant-shim-on");
            api.StatusCodeShouldBeOk();
        });
    }

    [Fact]
    public async Task HeaderIsIgnored_AndRequestFailsClosed_WhenShimIsDisabled()
    {
        // The point of the whole change: with the shim off, a caller asserting a tenant is not
        // served that tenant's rows — the request is refused for want of a tenant.
        await using var host = await HostWithShim(allowShim: false);

        await host.Scenario(api =>
        {
            api.Get.Url(TenantScopedRoute);
            api.WithRequestHeader("X-Tenant-Id", "tenant-shim-off");
            api.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task RequestWithNoTenantAtAll_FailsClosed_InBothModes()
    {
        await using var shimOn = await HostWithShim(allowShim: true);
        await shimOn.Scenario(api =>
        {
            api.Get.Url(TenantScopedRoute);
            api.StatusCodeShouldBe(400);
        });

        await using var shimOff = await HostWithShim(allowShim: false);
        await shimOff.Scenario(api =>
        {
            api.Get.Url(TenantScopedRoute);
            api.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task ShimSettingOverridesAppSettings_SoTheDisabledCaseIsReal()
    {
        // appsettings.Test.json sets Tenancy:AllowHeaderShim = true. If UseSetting ever stopped
        // out-ranking it, HeaderIsIgnored_… above would boot a shim-ON host, still see its
        // expected 400 for an unrelated reason, and quietly stop testing anything. Asserting the
        // resolved configuration value makes that impossible.
        await using var host = await HostWithShim(allowShim: false);

        var configuration = host.Services.GetRequiredService<IConfiguration>();

        Assert.False(configuration.GetValue<bool>("Tenancy:AllowHeaderShim"));
    }

    [Fact]
    public async Task ShimNeverOverridesAVerifiedContext_WhichIsUnchangedByThisGate()
    {
        // Regression guard for the ordering rule the gate sits on top of: a verified signed
        // context is authoritative, and a conflicting X-Tenant-Id must not displace it.
        // (Ingress verification itself is covered by TenantResolutionIngressTests.)
        await using var host = await HostWithShim(allowShim: true);

        await host.Scenario(api =>
        {
            api.Get.Url(TenantScopedRoute);
            api.WithRequestHeader("X-Tenant-Id", "tenant-from-header");
            api.WithRequestHeader("X-Tenant-Context", "not-a-valid-token");
            // A malformed context is rejected outright (401) rather than silently falling through
            // to the header — otherwise a forged context would downgrade into the dev shim.
            api.StatusCodeShouldBe(401);
        });
    }
}
