using Cordia.Domain.Fulfillment;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Seam for the spec-06 view of the spec-07 atomic hold / conditional-write capacity check
/// (spec 06 §6.3, FM#8 — no capacity).
/// <para>
///   Spec 07 replaces the implementation with the real conditional write against the
///   availability/inventory store. For T6 the only implementation is
///   <c>AlwaysAvailableCapacityService</c> (always returns <see cref="CapacityResult.Reserved"/>),
///   which lets tests override with a fake that returns <see cref="CapacityResult.NoCapacity"/>.
/// </para>
/// </summary>
public interface ICapacityService
{
    /// <summary>
    /// Attempts to reserve capacity for <paramref name="operationId"/> under
    /// <paramref name="tenantId"/>. Returns <see cref="CapacityResult.Reserved"/> on success or
    /// <see cref="CapacityResult.NoCapacity"/> when no availability remains.
    /// </summary>
    Task<CapacityResult> TryReserveAsync(
        string tenantId,
        string operationId,
        CancellationToken ct = default);
}
