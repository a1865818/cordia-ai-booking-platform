using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for the <see cref="VerificationGrant"/> aggregate.</summary>
public interface IVerificationGrantRepository
{
    Task AddAsync(VerificationGrant grant, CancellationToken ct = default);

    /// <summary>
    /// Returns the first valid grant matching the supplied criteria (Inv-11):
    /// customer_ref + allowed_action match, (target_booking_id == targetBookingId OR grant has
    /// no target_booking_id), now &lt; expires_at, consumed_at is null.
    /// Returns null when no qualifying grant exists.
    /// </summary>
    Task<VerificationGrant?> FindValidGrantAsync(
        string tenantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the specific presented grant only when it is valid for the supplied criteria.
    /// Used by the spec 08 authorization gate; callers must not authorize from "any" grant
    /// when a request presents a concrete grant id.
    /// </summary>
    Task<VerificationGrant?> FindValidGrantByIdAsync(
        string tenantId,
        string grantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>
    /// Identical scope check to <see cref="FindValidGrantByIdAsync"/> — same tenant, customer,
    /// action, target and expiry — but it does NOT require the grant to be unconsumed.
    ///
    /// <para>For a caller that consumes the grant LATER, at the moment it commits. Such a caller
    /// cannot pre-check with the unconsumed variant: its own successful first attempt spends the
    /// grant, so an honest retry of that same request would then be refused, turning an
    /// idempotent replay into a 403. Consumption is still enforced — by
    /// <see cref="ConsumeAsync"/>'s conditional update, which is the atomic single-use fence — this
    /// only stops the pre-check from double-counting it. A caller that does not consume must use
    /// <see cref="FindValidGrantByIdAsync"/>.</para>
    /// </summary>
    Task<VerificationGrant?> FindGrantByIdIgnoringConsumptionAsync(
        string tenantId,
        string grantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>
    /// The assurance level recorded on a grant, or <c>null</c> when no unexpired grant with that
    /// id exists for the tenant.
    ///
    /// Narrower than <see cref="FindValidGrantByIdAsync"/> on purpose: this answers "how well was
    /// this party verified", not "may this party perform this action". Acceptance needs the
    /// former and has none of the customer-ref / action / target inputs the authorization gate
    /// requires, so reusing that method would mean inventing them.
    /// </summary>
    Task<VerificationLevel?> GetAssuranceLevelAsync(
        string tenantId,
        string grantId,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>
    /// The assurance level recorded on a grant, but only when that grant was issued to
    /// <paramref name="customerRef"/> — otherwise <c>null</c>.
    ///
    /// <para>For a caller that has a customer to bind against but no authorization gate to lean
    /// on. <see cref="GetAssuranceLevelAsync"/> scopes to tenant + id + expiry, which is all it
    /// can do; on a path with no gate that lets a caller name ANY unexpired grant in the tenant
    /// and have its level recorded as their own. Action and target stay unconstrained on purpose:
    /// the question is "how well was this party verified", not "may this party act".</para>
    /// </summary>
    Task<VerificationLevel?> GetAssuranceLevelForCustomerAsync(
        string tenantId,
        string grantId,
        string customerRef,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>
    /// Single-use consumption: atomically sets consumed_at where consumed_at is null AND the grant
    /// is not yet expired (<paramref name="consumedAt"/> &lt; expires_at, §9.3). Returns rows-affected
    /// (1 = consumed, 0 = already consumed OR expired → caller must force re-verification).
    /// </summary>
    Task<int> ConsumeAsync(
        string tenantId,
        string grantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId,
        DateTimeOffset consumedAt,
        CancellationToken ct = default);
}
