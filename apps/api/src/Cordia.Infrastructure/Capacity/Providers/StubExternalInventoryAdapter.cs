using Cordia.Domain.Capacity;
using Cordia.Domain.Enums;

namespace Cordia.Infrastructure.Capacity.Providers;

/// <summary>
/// Stub implementation of <see cref="IExternalInventoryAdapter"/> for the NativeHold rung
/// (spec 07 §6.3). Returns a synthetic <see cref="ReservationOutcome.Held"/> outcome with a
/// fabricated <c>provider_ref</c> to illustrate the external-inventory ladder without touching
/// the database or a real provider.
/// <para>
/// This adapter is NOT invoked by the live workflow — the first-party path is live (spec 07
/// open-decision §13 #2). It exists for unit-testing the ladder scaffolding and as a template
/// for future real-provider adapters (which will persist/mirror the hold and wire into the
/// resolver per tenant/provider).
/// </para>
/// </summary>
public sealed class StubExternalInventoryAdapter : IExternalInventoryAdapter
{
    /// <inheritdoc/>
    public string ProviderName => "stub_external";

    /// <inheritdoc/>
    public CapacityPrimitive SupportedPrimitive => CapacityPrimitive.NativeHold;

    /// <inheritdoc/>
    /// <remarks>
    /// Returns a <see cref="ReservationStatus.Held"/> outcome carrying a synthetic
    /// <see cref="CapacityHold"/> with a fabricated <c>provider_ref</c>. The hold has
    /// <c>SlotId = null</c> (external-only, no first-party slot row) and
    /// <c>ProviderRef</c> set to a unique opaque reference. A real provider adapter would call
    /// the provider API, mirror the returned hold reference into <c>capacity_holds</c>, and
    /// return the persisted entity; that persistence step belongs in a future ticket.
    /// </remarks>
    public Task<ReservationOutcome> ReserveAsync(ReserveRequest req, CancellationToken ct = default)
    {
        var hold = new CapacityHold
        {
            TenantId = req.TenantId,
            HoldId = $"hold_{Guid.NewGuid():N}",
            OperationId = req.OperationId,
            WorkflowStep = req.WorkflowStep,
            SlotId = null, // external-only hold; no first-party capacity_slots row
            ReservedCapacity = req.RequestedCapacity,
            Status = HoldStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = req.ExpiresAt,
            ReleaseReason = null,
            ProviderRef = $"provref_{Guid.NewGuid():N}",
            Version = 0,
        };

        return Task.FromResult(ReservationOutcome.Held(hold));
    }
}
