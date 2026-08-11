using Cordia.Application.Common.Authorization;
using MediatR;

namespace Cordia.Application.Handlers.Waitlist.Accept;

/// <summary>
/// Customer acceptance of a live waitlist offer (spec 11 §5.1). Finalizes the temporary hold and
/// confirms the booking. Idempotent (Inv-5): re-accepting an accepted offer is a no-op.
/// </summary>
public sealed partial record AcceptWaitlistOfferCommand(
    string TenantId,
    string OfferId,
    string Actor,
    string ActorRole,
    string ReasonCode) : IRequest<Unit>, IOperatorActionRequest;

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
public sealed partial record AcceptWaitlistOfferCommand
{
    /// <summary>Operator identity from X-Actor; the interface name for the <c>Actor</c> parameter.</summary>
    public string ActorId => Actor;

    /// <inheritdoc />
    public string RequiredRole => StaffRole.Operator;

    /// <inheritdoc />
    public string ActionName => "waitlist.offer_accepted";

    /// <inheritdoc />
    public string TargetType => "waitlist_offer";

    /// <inheritdoc />
    public string TargetId => OfferId;
}
