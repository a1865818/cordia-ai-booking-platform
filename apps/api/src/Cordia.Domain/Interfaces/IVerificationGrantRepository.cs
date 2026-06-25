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
    /// Single-use consumption: sets consumed_at where consumed_at is null.
    /// Returns rows-affected (1 = consumed, 0 = already consumed — single-use guarantee).
    /// </summary>
    Task<int> ConsumeAsync(
        string tenantId,
        string grantId,
        DateTimeOffset consumedAt,
        CancellationToken ct = default);
}
