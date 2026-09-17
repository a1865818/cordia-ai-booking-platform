#pragma warning disable xUnit1051
using System.Net;
using Alba;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using Cordia.Tests.IntegrationTests.Acceptance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

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
public class OperatorReadGuardTests(AcceptanceApiFixture fixture)
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
    [InlineData("/api/Bookings/schedule?from=2031-01-01T00:00:00Z&to=2031-01-08T00:00:00Z")]
    [InlineData("/api/booking-operations")]
    [InlineData("/api/booking-operations/work-items")]
    [InlineData("/api/conversation-sessions")]
    [InlineData("/api/waitlist/enrollments")]
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

    /// <summary>
    /// Mutations are NOT refused by the controller guard. They are operator actions, and
    /// <c>OperatorActionBehavior</c> refuses them itself — writing the DENY audit row the spec-14
    /// §24.5 trail requires. A controller guard in front of it refused first and left no record.
    /// </summary>
    [Fact]
    public async Task AnUnprovenRefund_IsRefused_AndTheRefusalIsAudited()
    {
        await using var host = await ProductionLikeHostAsync();
        var tenantId = $"t-opguard-refund-{Guid.NewGuid():N}";

        await host.Scenario(x =>
        {
            x.Post.Url($"/api/deposits/dep-{Guid.NewGuid():N}/refund");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "someone");
            x.WithRequestHeader("X-Actor-Role", "owner");
            x.WithRequestHeader("X-Reason-Code", "customer_request");
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });

        await using var ctx = fixture.CreateContext(tenantId);
        var denials = await ctx.AuditEvents.AsNoTracking().IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId)
            .Select(a => a.Metadata)
            .ToListAsync();
        Assert.Contains(denials, m => m != null && m.Contains("identity_unverified"));
    }

    /// <summary>
    /// Every guarded read must ADVERTISE the 403 it can return, or the generated OpenAPI contract
    /// (and the typed web client built from it) tells consumers the refusal cannot happen.
    /// </summary>
    [Theory]
    [InlineData("api/customers")]
    [InlineData("api/customers/{customerRef}")]
    [InlineData("api/deposits")]
    [InlineData("api/payouts/summary")]
    [InlineData("api/reporting/funnel")]
    [InlineData("api/reporting/overview")]
    [InlineData("api/reporting/kpis")]
    [InlineData("api/Bookings/schedule")]
    [InlineData("api/Bookings/{bookingId}/notifications")]
    [InlineData("api/booking-operations")]
    [InlineData("api/booking-operations/work-items")]
    [InlineData("api/booking-operations/{operationId}/notifications")]
    [InlineData("api/conversation-sessions")]
    [InlineData("api/waitlist/enrollments")]
    public async Task EveryGuardedRead_DeclaresTheForbiddenResponse(string route)
    {
        await using var host = await ProductionLikeHostAsync();
        var descriptions = host.Services
            .GetRequiredService<Microsoft.AspNetCore.Mvc.ApiExplorer.IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items.SelectMany(g => g.Items)
            .Where(d => string.Equals(d.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.RelativePath, route, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var description = Assert.Single(descriptions);
        Assert.Contains(description.SupportedResponseTypes, r => r.StatusCode == StatusCodes.Status403Forbidden);
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
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("	")]
    public void RequireOperator_TreatsABlankActorIdAsUnproven(string blank)
        => Assert.Throws<Cordia.Domain.Exceptions.AuthorizationFailedException>(
            () => OperatorGuard.RequireOperator(
                new Actor(blank, "owner"), allowAsserted: false, "test.read"));
}
