using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Security;

/// <summary>
/// Fail-closed guard for console reads that expose customer PII or commercial figures
/// (#72, Inv-11). Sits beside <see cref="TenantGuard"/>: that one answers "which tenant",
/// this one answers "is anyone actually signed in".
///
/// <para><b>Why these reads need their own guard rather than
/// <c>IOperatorActionRequest</c>.</b> That interface is the spec-14 §24.5 spine for operator
/// <i>actions</i>, and it mandates a non-empty <c>X-Reason-Code</c> plus an ALLOW audit per
/// invocation. Both are right for a refund or a menu publish and wrong for a page load: the
/// console's read calls (<c>/api/customers</c>, <c>/api/deposits</c>,
/// <c>/api/payouts/summary</c>, <c>/api/reporting/*</c>) go through <c>fetchApi</c> without
/// actor options at all, so adopting that interface would 403 every one of those screens — and
/// "the reason I am viewing the customer list" is not a thing an operator can meaningfully
/// supply. Auditing every grid refresh would also bury the deliberate ALLOW records that
/// mutations write.</para>
///
/// <para><b>What it checks.</b> A PROVEN identity — <see cref="IActorContext.ActorId"/> is
/// non-empty only when <c>TenantContextVerificationMiddleware</c> bound it from a
/// signature-verified, non-revoked <c>opctx+jws</c>. The console carries that session on every
/// request once signed in, whether or not the caller passed actor headers, so this costs the
/// web nothing.</para>
///
/// <para><b>The development escape is the same one, not a new one.</b>
/// <c>Authorization:AllowAssertedStaffRole</c> already governs whether an unproven assertion may
/// stand in for proof; it defaults ON in Development and OFF everywhere else. Passing
/// <paramref name="allowAsserted"/> keeps this guard on that single switch rather than inventing
/// a second one that could drift away from it.</para>
/// </summary>
public static class OperatorGuard
{
    /// <summary>
    /// Throws <see cref="AuthorizationFailedException"/> unless the caller holds a verified
    /// operator session, or asserted identities are explicitly permitted.
    /// </summary>
    /// <param name="actor">The request's actor context.</param>
    /// <param name="allowAsserted">
    /// <c>Authorization:AllowAssertedStaffRole</c> — ON in Development, OFF elsewhere.
    /// </param>
    /// <param name="action">Audit/action name used in the refusal message.</param>
    public static void RequireOperator(IActorContext actor, bool allowAsserted, string action)
    {
        if (!string.IsNullOrEmpty(actor.ActorId) || allowAsserted)
            return;

        throw new AuthorizationFailedException(
            action,
            "This view requires a signed-in operator (spec 14 §24.1, Inv-11).");
    }
}
