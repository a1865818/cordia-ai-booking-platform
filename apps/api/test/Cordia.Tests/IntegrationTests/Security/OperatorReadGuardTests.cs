#pragma warning disable xUnit1051
using System.Net;
using Alba;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using Cordia.Tests.IntegrationTests.Acceptance;
using Microsoft.AspNetCore.Hosting;

namespace Cordia.Tests.IntegrationTests.Security;

/// <summary>
/// Console reads that expose customer PII or commercial figures must require a signed-in
/// operator, not merely a resolved tenant (#72, Inv-11).
///
/// <para><b>Why these are integration tests.</b> The guard's logic is trivial and unit-tested
/// below; what is worth proving against a real host is that the four controllers actually
/// <i>call</i> it, and that the response is a 403 rather than a silent 200 with someone else's
/// customer list. A mocked controller test would assert that a method was invoked, which is not
/// the same claim.</para>
///
/// <para><b>The whole test host normally defeats this.</b> <c>appsettings.Test.json:8</c> sets
/// <c>Authorization:AllowAssertedStaffRole: true</c>, so every other suite runs with asserted
/// identities permitted — correctly, because they are simulating an operator. These tests build
/// their own host with the flag OFF, which is the production default and the only configuration
/// in which the guard has anything to do.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class OperatorReadGuardTests
{
    /// <summary>A host configured the way a deployment is: asserted identities authorize nothing.</summary>
    private static Task<IAlbaHost> ProductionLikeHostAsync()
        => AlbaHost.For<Program>(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("ConnectionStrings:CordiaDBConnection", AcceptanceApiFixture.TestConnStr);
            builder.UseSetting("DatabaseBootstrap:MigrateOnStartup", "true");
            builder.UseSetting("Authorization:AllowAssertedStaffRole", "false");
        });

    /// <summary>
    /// Every read that exposes PII or money. Each was tenant-scoped only before this change:
    /// anyone who could name a tenant could read its customer list, deposit ledger, payout
    /// totals and booking funnel.
    /// </summary>
    [Theory]
    [InlineData("/api/customers")]
    [InlineData("/api/deposits")]
    [InlineData("/api/payouts/summary")]
    [InlineData("/api/reporting/funnel")]
    [InlineData("/api/reporting/overview")]
    [InlineData("/api/reporting/kpis")]
    [InlineData("/api/Bookings/bk-any/notifications")]
    [InlineData("/api/booking-operations/op-any/notifications")]
    public async Task ReadsAreRefused_WithoutAVerifiedOperatorSession(string url)
    {
        await using var host = await ProductionLikeHostAsync();
        var tenantId = $"t-opguard-{Guid.NewGuid():N}";

        await host.Scenario(x =>
        {
            x.Get.Url(url);
            // A resolved tenant and nothing else — precisely the caller these reads used to serve.
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }

    /// <summary>
    /// The paired negative. Asserted identities are permitted in Development and in the rest of
    /// the test host, and these reads must keep working there — otherwise the guard would be
    /// indistinguishable from simply breaking the console.
    /// </summary>
    [Theory]
    [InlineData("/api/customers")]
    [InlineData("/api/payouts/summary")]
    public async Task ReadsStillWork_WhenAssertedIdentitiesArePermitted(string url)
    {
        await using var host = await AlbaHost.For<Program>(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("ConnectionStrings:CordiaDBConnection", AcceptanceApiFixture.TestConnStr);
            builder.UseSetting("DatabaseBootstrap:MigrateOnStartup", "true");
            builder.UseSetting("Authorization:AllowAssertedStaffRole", "true");
        });

        var tenantId = $"t-opguard-ok-{Guid.NewGuid():N}";

        await host.Scenario(x =>
        {
            x.Get.Url(url);
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });
    }

    // ── The guard itself ───────────────────────────────────────────────────────────────────

    private sealed record Actor(string? ActorId, string? ActorRole) : IActorContext;

    [Fact]
    public void RequireOperator_AcceptsAProvenSession_EvenWithTheEscapeOff()
        => OperatorGuard.RequireOperator(
            new Actor("ops.jane", "operator"), allowAsserted: false, "test.read");

    [Fact]
    public void RequireOperator_AcceptsAnAssertedActor_WhenTheEscapeIsOn()
        => OperatorGuard.RequireOperator(
            new Actor(null, null), allowAsserted: true, "test.read");

    [Fact]
    public void RequireOperator_RefusesAnUnprovenActor_WhenTheEscapeIsOff()
        => Assert.Throws<Cordia.Domain.Exceptions.AuthorizationFailedException>(
            () => OperatorGuard.RequireOperator(
                new Actor(null, null), allowAsserted: false, "test.read"));

    /// <summary>
    /// An empty string is not a session. `IActorContext.ActorId` is bound from a JWS claim, and a
    /// claim that decoded to blank must not read as proof.
    /// </summary>
    [Fact]
    public void RequireOperator_TreatsAnEmptyActorIdAsUnproven()
        => Assert.Throws<Cordia.Domain.Exceptions.AuthorizationFailedException>(
            () => OperatorGuard.RequireOperator(
                new Actor(string.Empty, "owner"), allowAsserted: false, "test.read"));
}
