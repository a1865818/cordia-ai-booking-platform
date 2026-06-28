using Cordia.Domain.Entities;

namespace Cordia.Domain.Capacity;

/// <summary>Status of a <see cref="ICapacityReservation.ReserveAsync"/> call.</summary>
public enum ReservationStatus
{
    /// <summary>Capacity was reserved and an active hold was created.</summary>
    Held,

    /// <summary>Insufficient remaining capacity; the operation must route to AlternativesRequired.</summary>
    NoCapacity,

    /// <summary>
    /// The provider returned an ambiguous outcome (e.g. AtomicBook/CheckThenCreate rung);
    /// the operation must route to Reconciling keyed by <see cref="ReservationOutcome.ProviderRef"/>.
    /// </summary>
    Ambiguous,
}

/// <summary>
/// Sealed discriminated result from <see cref="ICapacityReservation.ReserveAsync"/>. Inspect
/// <see cref="Status"/> to branch, then access <see cref="Hold"/> or <see cref="ProviderRef"/>
/// as appropriate.
/// </summary>
public sealed record ReservationOutcome
{
    /// <summary>The outcome status of the reservation attempt.</summary>
    public ReservationStatus Status { get; init; }

    /// <summary>The created active hold; populated when <see cref="Status"/> is <see cref="ReservationStatus.Held"/>.</summary>
    public CapacityHold? Hold { get; init; }

    /// <summary>Provider reference for reconciliation; populated when <see cref="Status"/> is <see cref="ReservationStatus.Ambiguous"/>.</summary>
    public string? ProviderRef { get; init; }

    private ReservationOutcome() { }

    /// <summary>Creates a <see cref="ReservationStatus.Held"/> outcome with the given active hold.</summary>
    public static ReservationOutcome Held(CapacityHold hold) =>
        new() { Status = ReservationStatus.Held, Hold = hold };

    /// <summary>Creates a <see cref="ReservationStatus.NoCapacity"/> outcome.</summary>
    public static ReservationOutcome NoCapacity() =>
        new() { Status = ReservationStatus.NoCapacity };

    /// <summary>
    /// Creates a <see cref="ReservationStatus.Ambiguous"/> outcome with the provider reference
    /// that should be used to reconcile the operation (Inv-7).
    /// </summary>
    public static ReservationOutcome Ambiguous(string providerRef) =>
        new() { Status = ReservationStatus.Ambiguous, ProviderRef = providerRef };
}
