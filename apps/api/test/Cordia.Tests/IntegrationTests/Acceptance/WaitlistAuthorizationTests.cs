#pragma warning disable xUnit1051
using System.Net;
using Alba;

namespace Cordia.Tests.IntegrationTests.Acceptance;

/// <summary>
/// The waitlist mutations are operator actions (spec 14 §24.5, GD-1).
///
/// <para>They were not. The controller carried a standing <c>Authorization is a TODO</c>, and
/// <c>ResolveActor</c> turned a MISSING <c>X-Actor</c> into the literal string <c>"customer"</c>
/// and proceeded — so any caller who knew a tenant id could enrol, cancel, accept or decline on a
/// guest's behalf, and the audit trail recorded an identity the request never established. That
/// last part is the worse half: a record that asserts more than the platform knew is not a weaker
/// trail, it is a misleading one.</para>
///
/// <para>This is distinct from the accepted console-authentication caveat. That caveat is about
/// <b>authentication</b> — the <c>X-Actor</c> shim is only trustworthy in Development. This was
/// about <b>authorization</b>: even given a trusted actor, no role was ever checked.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class WaitlistAuthorizationTests(AcceptanceApiFixture fixture)
{
    private static object EnrolBody() => new
    {
        customer_ref = "+61400111222",
        acceptable_from = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero),
        acceptable_to = new DateTimeOffset(2026, 9, 1, 17, 0, 0, TimeSpan.Zero),
        party_size = 2,
        location_id = "loc-1",
        service_id = "svc-1",
        notification_channel = "sms",
        offer_lifetime_seconds = 900,
    };

    private Task<IScenarioResult> EnrolAsync(
        string tenantId,
        HttpStatusCode expected,
        string? actor = null,
        string? role = null,
        string? reason = null)
        => fixture.Host.Scenario(x =>
        {
            x.Post.Json(EnrolBody()).ToUrl("/api/waitlist/enrollments");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            if (actor is not null) x.WithRequestHeader("X-Actor", actor);
            if (role is not null) x.WithRequestHeader("X-Actor-Role", role);
            if (reason is not null) x.WithRequestHeader("X-Reason-Code", reason);
            x.StatusCodeShouldBe(expected);
        });

    /// <summary>The exact hole: no identity at all used to be accepted and audited as "customer".</summary>
    [Fact]
    public async Task AnAnonymousCallerCannotEnrolAGuest()
    {
        await EnrolAsync(TestIds.New("wl-authz-anon"), HttpStatusCode.Forbidden);
    }

    /// <summary>An actor without a role asserts who but not as-what — fail closed.</summary>
    [Fact]
    public async Task AnActorWithNoRoleCannotEnrolAGuest()
    {
        await EnrolAsync(
            TestIds.New("wl-authz-norole"), HttpStatusCode.Forbidden,
            actor: "staff@example.com", reason: "waitlist_enrolment");
    }

    /// <summary>A reason code is part of the spine (§24.5), not optional decoration.</summary>
    [Fact]
    public async Task AnActorWithNoReasonCodeCannotEnrolAGuest()
    {
        await EnrolAsync(
            TestIds.New("wl-authz-noreason"), HttpStatusCode.Forbidden,
            actor: "staff@example.com", role: "operator");
    }

    /// <summary>An unrecognised role satisfies nothing — the matrix is fail-closed (GD-1).</summary>
    [Fact]
    public async Task AnUnknownRoleCannotEnrolAGuest()
    {
        await EnrolAsync(
            TestIds.New("wl-authz-badrole"), HttpStatusCode.Forbidden,
            actor: "staff@example.com", role: "manager", reason: "waitlist_enrolment");
    }

    /// <summary>
    /// The console's own call must still work. Operator suffices — enrolling a guest is routine
    /// front-desk work, not an owner-only commercial decision.
    /// </summary>
    [Fact]
    public async Task AnOperatorMayEnrolAGuest()
    {
        await EnrolAsync(
            TestIds.New("wl-authz-op"), HttpStatusCode.Created,
            actor: "staff@example.com", role: "operator", reason: "waitlist_enrolment");
    }

    /// <summary>Owner satisfies operator (owner ⊇ operator).</summary>
    [Fact]
    public async Task AnOwnerMayEnrolAGuest()
    {
        await EnrolAsync(
            TestIds.New("wl-authz-owner"), HttpStatusCode.Created,
            actor: "owner@example.com", role: "owner", reason: "waitlist_enrolment");
    }

    /// <summary>
    /// Cancelling someone's enrolment is gated the same way as creating one. The bad-role case is
    /// asserted rather than the anonymous one so this pins the AUTHORIZATION layer: the enrolment
    /// id does not exist, so reaching the handler would give 404, not 403.
    /// </summary>
    [Fact]
    public async Task AnActorWithABadRoleCannotCancelAnEnrolment()
    {
        var tenantId = TestIds.New("wl-authz-cxl");

        await fixture.Host.Scenario(x =>
        {
            x.Delete.Url("/api/waitlist/enrollments/wle_whatever");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "staff@example.com");
            x.WithRequestHeader("X-Actor-Role", "manager");
            x.WithRequestHeader("X-Reason-Code", "trying it on");
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }

    /// <summary>
    /// Accepting an offer books a table — the offer id used to be the only thing between an
    /// anonymous caller and a confirmed booking.
    ///
    /// <para>The refusal must be a <b>403 from the authorization layer</b>, not a 400 from
    /// validation, and the difference is not cosmetic. Pipeline order is Validation →
    /// OperatorAction → Handler, so a <c>NotEmpty(Actor)</c> validator rule answers first and
    /// returns — before <c>OperatorActionBehavior</c> can deny-audit the attempt (spec 14 §3.1
    /// step 5). An unauthorized caller would be turned away with no record of having tried, which
    /// is the one outcome an authorization spine must never produce. The validators for these
    /// commands therefore deliberately carry no actor rule, and this test is what keeps it that
    /// way: re-adding one turns this 403 back into a 400.</para>
    /// </summary>
    [Theory]
    [InlineData("accept")]
    [InlineData("decline")]
    public async Task AnAnonymousCallerCannotActOnAnOffer(string action)
    {
        var tenantId = TestIds.New($"wl-authz-{action}");

        await fixture.Host.Scenario(x =>
        {
            x.Post.Json(new { }).ToUrl($"/api/waitlist/offers/op_wl_whatever/{action}");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }

    /// <summary>
    /// The authorization layer proper: a real actor asserting a role that satisfies nothing is a
    /// 403, and it never reaches the handler — note the offer id does not exist, so a pass-through
    /// would have produced 404 instead.
    /// </summary>
    [Theory]
    [InlineData("accept")]
    [InlineData("decline")]
    public async Task AnActorWithABadRoleCannotAcceptAnOffer(string action)
    {
        var tenantId = TestIds.New($"wl-authz-{action}-role");

        await fixture.Host.Scenario(x =>
        {
            x.Post.Json(new { }).ToUrl($"/api/waitlist/offers/op_wl_whatever/{action}");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "staff@example.com");
            x.WithRequestHeader("X-Actor-Role", "manager");
            x.WithRequestHeader("X-Reason-Code", "trying it on");
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }

    /// <summary>
    /// The list is NOT an operator action: it needs no role and no reason code, because an operator
    /// needs it to work from. It IS an operator read (#72) — it carries every guest's customer_ref —
    /// so outside Development it requires a signed-in session. This host permits asserted identities,
    /// so a bare tenant-scoped GET still succeeds here; the refusal without a session is pinned by
    /// <c>OperatorReadGuardTests.ReadsAreRefused_WithoutAVerifiedOperatorSession</c>.
    /// </summary>
    [Fact]
    public async Task TheEnrollmentList_NeedsNoRoleOrReasonCode()
    {
        var tenantId = TestIds.New("wl-authz-read");

        await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/waitlist/enrollments");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });
    }
}
