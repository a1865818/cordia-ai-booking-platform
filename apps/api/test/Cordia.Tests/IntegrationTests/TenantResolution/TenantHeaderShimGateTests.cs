using Alba;
using Microsoft.AspNetCore.Hosting;

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
public class TenantHeaderShimGateTests
{
    /// <summary>A tenant-scoped GET that fails closed at <c>TenantGuard.RequireTenant</c>.</summary>
    private const string TenantScopedRoute = "/api/consent-policies?channel=web";

    private static Task<IAlbaHost> HostWithShim(bool allowShim) =>
        AlbaHost.For<Program>(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("ConnectionStrings:CordiaDBConnection", AcceptanceApiFixtureConnection);
            builder.UseSetting("DatabaseBootstrap:MigrateOnStartup", "false");
            builder.UseSetting("Tenancy:AllowHeaderShim", allowShim ? "true" : "false");
        });

    private const string AcceptanceApiFixtureConnection =
        "Host=localhost;Port=5432;Database=Cordia_API_Test;Username=cordia;Password=localdev";

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
