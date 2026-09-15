using Cordia.Domain.Capacity;
using Cordia.Domain.Generated;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Resolves which capacity primitive ladder rung applies for a given (tenant, provider, slot-source)
/// triple and maps each rung to an availability mode (spec 07 §6.3). Configured per (tenant,
/// provider) in <c>tenant_capacity_primitives</c>; the §13 default is first-party (#106).
/// </summary>
public interface ICapacityPrimitiveResolver
{
    /// <summary>
    /// Returns the <see cref="CapacityPrimitive"/> rung that governs how a reservation should be
    /// made for the given combination of tenant, provider name, and slot source.
    /// </summary>
    /// <param name="tenantId">Tenant making the reservation.</param>
    /// <param name="providerName">The booking provider (e.g. "first_party", "stub_external").</param>
    /// <param name="slotSource">Optional slot-source hint from the canonical payload (e.g. resource_id).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CapacityPrimitive> ResolveAsync(
        string tenantId,
        string providerName,
        string? slotSource,
        CancellationToken ct = default);

    /// <summary>
    /// Maps a <see cref="CapacityPrimitive"/> rung to the <see cref="AvailabilityModeEnum"/> that
    /// a successful reservation on that rung produces (spec 07 §5, table).
    /// </summary>
    AvailabilityModeEnum ModeFor(CapacityPrimitive primitive);
}
