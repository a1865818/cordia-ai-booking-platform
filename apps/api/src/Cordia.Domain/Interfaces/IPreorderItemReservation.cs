using Cordia.Domain.Generated;

namespace Cordia.Domain.Interfaces;

/// <summary>Outcome of a pre-order item reservation attempt.</summary>
public enum ItemReservationStatus
{
    /// <summary>All lines reserved against the (first-party / provider) inventory boundary.</summary>
    Reserved,

    /// <summary>One or more items could not be reserved (out of stock at fulfillment time).</summary>
    Unavailable,
}

/// <summary>
/// Reserves the pre-order's catalog items against the fulfillment inventory boundary (spec 11 §5.3).
/// Per the scope decision this is a stub-ported first-party port (no real POS); the default
/// implementation is deterministic and a real adapter swaps in later (matches <c>Stub*Adapter</c>).
/// Reservation + release are keyed by <c>(tenantId, sagaId)</c> so both are idempotent under
/// duplicate delivery (Inv-5/Inv-9) — no reservation id needs to be persisted on the saga.
/// </summary>
public interface IPreorderItemReservation
{
    /// <summary>Reserves every line in <paramref name="reference"/>; idempotent for the same saga.</summary>
    Task<ItemReservationStatus> ReserveAsync(
        string tenantId, string sagaId, PreorderReference reference, CancellationToken ct = default);

    /// <summary>Releases any reservation held for the saga; idempotent no-op if none/already released.</summary>
    Task ReleaseAsync(string tenantId, string sagaId, CancellationToken ct = default);
}
