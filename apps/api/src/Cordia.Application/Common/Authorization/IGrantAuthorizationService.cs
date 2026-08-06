using Cordia.Domain.Entities;
using Cordia.Domain.Generated;

namespace Cordia.Application.Common.Authorization;

/// <summary>Application authorization gate for spec 08 verification grants.</summary>
/// <remarks><c>targetType</c> is REQUIRED (no default): it is the resource the grant authorizes
/// ("booking" for manage-booking flows, "conversation_session" for a transcript read) and drives the
/// deny audit's TargetType + DataClasses. It is deliberately not defaulted so a new non-booking
/// caller cannot silently mis-attribute its denials to a booking (the defect this parameter fixed).</remarks>
public interface IGrantAuthorizationService
{
    Task<VerificationGrant> RequireGrantAsync(
        string tenantId,
        string grantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId,
        string targetType,
        CancellationToken ct = default);

    /// <summary>
    /// Scope-only check for a caller that consumes the grant later, inside its own commit
    /// transaction (see <see cref="ConsumeGrantAsync"/>). Denies — and deny-audits — exactly as
    /// <see cref="RequireGrantAsync"/> does on tenant/customer/action/target/expiry, but tolerates
    /// an already-consumed grant so a legitimate idempotent replay of the SAME request is not
    /// refused by the fence its own first attempt set. Single-use is still enforced: a NEW request
    /// presenting a spent grant reaches <see cref="ConsumeGrantAsync"/>, which affects zero rows
    /// and denies.
    /// </summary>
    Task<VerificationGrant> RequireGrantScopeAsync(
        string tenantId,
        string grantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId,
        string targetType,
        CancellationToken ct = default);

    /// <summary>
    /// Records a DENY for a refusal raised BEFORE any grant lookup — an unknown target, or one
    /// carrying nothing to authorize against (spec 14 §3.1 step 5: every DENY is recorded).
    ///
    /// <para>Here rather than in the calling handler so every denial on this door shares one
    /// implementation and, more importantly, one durability guarantee: the row is written through a
    /// FRESH DI scope, so it survives the caller's transaction being rolled back by the
    /// <see cref="Cordia.Domain.Exceptions.VerificationRequiredException"/> that follows it.
    /// A copy over the request-scoped writer would vanish the moment authorization moved inside a
    /// transaction — which is exactly what grant CONSUMPTION already did.</para>
    /// </summary>
    Task WriteDenyAsync(
        string tenantId,
        string? grantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId,
        string targetType,
        string reason,
        string? actorId = null,
        string? actorRole = null);

    Task ConsumeGrantAsync(
        string tenantId,
        string grantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId,
        string targetType,
        CancellationToken ct = default);
}
