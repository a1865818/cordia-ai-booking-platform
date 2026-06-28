using Cordia.Domain.Capacity;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for first-party capacity slot entities.</summary>
public interface ICapacitySlotRepository
{
    /// <summary>Returns the slot for the given (tenantId, slotId) pair, or null if not found. Uses AsNoTracking.</summary>
    Task<CapacitySlot?> GetAsync(string tenantId, string slotId, CancellationToken ct = default);

    /// <summary>Inserts a new capacity slot and flushes within the ambient transaction.</summary>
    Task AddAsync(CapacitySlot slot, CancellationToken ct = default);
}
