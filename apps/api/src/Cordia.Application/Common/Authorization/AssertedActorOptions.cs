namespace Cordia.Application.Common.Authorization;

/// <summary>
/// Whether an ASSERTED staff role may stand in for a verification grant (spec 08 §12, Inv-11).
///
/// <para><c>X-Actor</c> / <c>X-Actor-Role</c> are client-supplied strings. Nothing on the request
/// binds them to an authenticated principal — <c>StaffRole.Satisfies</c> checks the vocabulary, not
/// the caller — so wherever the role branch is live, anyone who knows a booking id can send
/// <c>X-Actor-Role: operator</c> and reschedule someone else's booking with no grant at all. That is
/// acceptable on a developer machine and in the test host, where the console's staff identity is a
/// documented shim; it is unauthenticated mutation of another customer's booking anywhere else.</para>
///
/// <para>So it is gated exactly like <c>Tenancy:AllowHeaderShim</c>, and defaults the same way: ON
/// in Development, OFF everywhere else unless a deployment sets it explicitly. This costs no
/// working configuration — the console cannot name a tenant outside Development either, so it is
/// already Development-only in practice — and closes the path in production, where a mutation then
/// requires a real server-issued grant.</para>
///
/// <para>Superseded by signed actor context (spec 08/15). When an actor can be PROVEN, the role
/// branch stops being an assertion and this gate goes away.</para>
/// </summary>
public sealed class AssertedActorOptions
{
    public const string SectionName = "Authorization";

    /// <summary>
    /// When false, a mutation must present a verification grant; an asserted role authorizes
    /// nothing. Bound from <c>Authorization:AllowAssertedStaffRole</c>.
    /// </summary>
    public bool AllowAssertedStaffRole { get; set; }
}
