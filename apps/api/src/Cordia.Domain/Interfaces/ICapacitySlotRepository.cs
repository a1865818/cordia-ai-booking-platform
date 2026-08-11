using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for first-party capacity slot entities.</summary>
public interface ICapacitySlotRepository
{
    /// <summary>Returns the slot for the given (tenantId, slotId) pair, or null if not found. Uses AsNoTracking.</summary>
    Task<CapacitySlot?> GetAsync(string tenantId, string slotId, CancellationToken ct = default);

    /// <summary>
    /// Slots starting within [<paramref name="from"/>, <paramref name="to"/>), ascending.
    /// Tenant-scoped, AsNoTracking. Backs the availability read.
    /// </summary>
    Task<IReadOnlyList<CapacitySlot>> ListAsync(
        string tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    /// <summary>Inserts a new capacity slot and flushes within the ambient transaction.</summary>
    Task AddAsync(CapacitySlot slot, CancellationToken ct = default);

    /// <summary>
    /// Inserts the slot, or returns <c>false</c> when another writer already created it. Makes the
    /// declaration endpoint's promised idempotency hold under a genuine race, where a pre-read plus
    /// insert cannot.
    /// </summary>
    Task<bool> TryAddAsync(CapacitySlot slot, CancellationToken ct = default);

    /// <summary>
    /// Takes the row lock on one slot WITHOUT changing it (<c>SELECT … FOR UPDATE</c>), so a
    /// transaction can order its acquisition of the capacity rows before it writes any of them.
    ///
    /// <para>The mirror of <see cref="ICapacityHoldRepository.LockAsync"/>, and needed for the same
    /// class of reason. Locking holds before slots removes the hold↔slot cycle, but it says nothing
    /// about the order of two SLOT rows — and a transaction that moves a booking between slots
    /// touches two. Two reschedules running in opposite directions between the same pair
    /// (<c>S1 → S2</c> and <c>S2 → S1</c>) each take one row and wait on the other, which is a cycle
    /// built entirely out of slots. Acquiring both in ascending slot-id order makes it unreachable.</para>
    ///
    /// <para>Prefer <see cref="ICapacityLockOrder"/> over calling this directly: it is what knows the
    /// order, and a hand-rolled acquisition is how the ordering rule gets broken.</para>
    /// </summary>
    Task LockAsync(string tenantId, string slotId, CancellationToken ct = default);
}
