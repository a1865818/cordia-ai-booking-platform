using Cordia.Domain.Capacity;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Adapter interface for an external booking-provider inventory system (spec 07 §6.3).
/// Each provider implements the strongest ladder rung it supports (NativeHold, IdempotentPending,
/// AtomicBook, or CheckThenCreate). The adapter mirrors the provider hold into
/// <c>capacity_holds</c> with a <c>provider_ref</c> for reconciliation (Inv-7).
/// </summary>
public interface IExternalInventoryAdapter
{
    /// <summary>Stable identifier that matches the provider name in <see cref="ReserveRequest.ProviderName"/>.</summary>
    string ProviderName { get; }

    /// <summary>The capacity primitive rung this adapter implements.</summary>
    CapacityPrimitive SupportedPrimitive { get; }

    /// <summary>
    /// Attempts to reserve capacity through the external provider and mirrors the result into
    /// a <see cref="CapacityHold"/> with <see cref="CapacityHold.ProviderRef"/> set.
    /// </summary>
    Task<ReservationOutcome> ReserveAsync(ReserveRequest req, CancellationToken ct = default);
}
