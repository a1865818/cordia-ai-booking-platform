using Cordia.Domain.Waitlist;
using Cordia.Application.Common.Authorization;
using MediatR;

namespace Cordia.Application.Handlers.Waitlist.Enroll;

/// <summary>
/// Enrolls a customer on the waitlist (spec 11 §4.4). Returns the new enrollment id. The optional
/// <see cref="Deposit"/> / <see cref="AutoBook"/> value objects are persisted as jsonb on the
/// enrollment. Auto-book is honoured only within the recorded grant's constraints (§20).
/// </summary>
public sealed partial record EnrollWaitlistCommand(
    string TenantId,
    string Actor,
    string CustomerRef,
    DateTimeOffset AcceptableFrom,
    DateTimeOffset AcceptableTo,
    int PartySize,
    string LocationId,
    string ServiceId,
    int OfferLifetimeSeconds,
    DepositTerms? Deposit,
    string NotificationChannel,
    AutoBookGrant? AutoBook,
    // Spec 11 §5.1 (divergence #1) — identity context stamped onto the acceptance confirmation.
    // Optional: default to CustomerRef / "unverified" so the accept never over-claims assurance.
    string? IdentityRef = null,
    string? VerificationLevel = null,
    /// <summary>
    /// Caller-supplied idempotency key. Optional: when absent one is derived from the enrolment's
    /// own material fields, so a blind retry of an identical enrolment still deduplicates.
    /// </summary>
    string? IdempotencyKey = null,
    string ActorRole = "",
    string ReasonCode = "") : IRequest<string>, IOperatorActionRequest;

/// <summary>
/// Operator-action spine (spec 14 §24.5, GD-1). Requires a real actor, the <c>operator</c> role
/// (owner satisfies it — front-desk work, not an owner-only commercial decision) and a reason
/// code; <c>OperatorActionBehavior</c> deny-audits and 403s otherwise.
///
/// <para>Added because the controller previously turned a MISSING <c>X-Actor</c> into the literal
/// string <c>"customer"</c> and proceeded. Any caller who knew a tenant id could enrol, cancel,
/// accept or decline on a guest's behalf, and the audit trail recorded an identity the request
/// never established — a record asserting more than the platform knew.</para>
///
/// <para>The console is the only client of these endpoints and already sends the actor headers;
/// the agent lane does not call them at all. If a guest-facing accept/decline surface is built
/// later it needs its own authorization design (an offer-scoped grant, which
/// <c>verification_grants</c> cannot express today — it scopes to a booking). Failing closed
/// is the right default until that exists.</para>
/// </summary>
public sealed partial record EnrollWaitlistCommand
{
    /// <summary>Operator identity from X-Actor; the interface name for the <c>Actor</c> parameter.</summary>
    public string ActorId => Actor;

    /// <inheritdoc />
    public string RequiredRole => StaffRole.Operator;

    /// <inheritdoc />
    public string ActionName => "waitlist.enrolled";

    /// <inheritdoc />
    public string TargetType => "waitlist_enrollment";

    /// <inheritdoc />
    public string TargetId => CustomerRef;
}
