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
}
