using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;

namespace Cordia.Infrastructure.Providers;

/// <summary>
/// Deterministic in-memory <see cref="IPreorderItemReservation"/> for development and as the default
/// (spec 11 §5.3; a real POS/inventory adapter swaps in later). Reserves successfully unless any line
/// item id contains the <c>"reserve_fail"</c> sentinel, which yields
/// <see cref="ItemReservationStatus.Unavailable"/> so the compensation path can be exercised
/// deterministically. Stateless (reservations are notionally keyed by saga id) → singleton; release
/// is a no-op the saga can call idempotently.
/// </summary>
public sealed class StubPreorderItemReservation : IPreorderItemReservation
{
    public Task<ItemReservationStatus> ReserveAsync(
        string tenantId, string sagaId, PreorderReference reference, CancellationToken ct = default)
    {
        var unavailable = reference.Lines?.Any(
            l => l.ItemId.Contains("reserve_fail", StringComparison.OrdinalIgnoreCase)) ?? false;
        return Task.FromResult(unavailable ? ItemReservationStatus.Unavailable : ItemReservationStatus.Reserved);
    }

    public Task ReleaseAsync(string tenantId, string sagaId, CancellationToken ct = default)
        => Task.CompletedTask;
}
