namespace Cordia.Domain.Capacity;

/// <summary>
/// Rungs of the external-inventory primitive ladder (spec 07 §6.3).
/// The resolver chooses the highest-fidelity rung the provider supports for a given
/// (tenant, provider, slot-source) triple and uses it to determine the resulting
/// availability mode.
/// </summary>
public enum CapacityPrimitive
{
    /// <summary>
    /// First-party Cordia inventory: a single conditional UPDATE against <c>capacity_slots</c>
    /// is the atomic guard (Inv-6). This is the default rung when no external provider is active.
    /// </summary>
    FirstParty,

    /// <summary>
    /// Provider supports a native expiring hold; the provider hold is mirrored into
    /// <c>capacity_holds</c> with a <c>provider_ref</c> (availability mode: Held).
    /// </summary>
    NativeHold,

    /// <summary>
    /// Provider supports idempotent create-pending; acts as a hold until confirmed
    /// (availability mode: Held).
    /// </summary>
    IdempotentPending,

    /// <summary>
    /// Provider supports atomic book only; ambiguous outcomes route to Reconciling
    /// (availability mode: Confirmed on clean success).
    /// </summary>
    AtomicBook,

    /// <summary>
    /// Provider supports check-then-create only (last resort); any ambiguity routes
    /// to Reconciling; never blind-retry (Inv-7).
    /// </summary>
    CheckThenCreate,
}
