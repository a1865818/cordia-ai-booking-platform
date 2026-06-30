using Cordia.Domain.Entities;
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
