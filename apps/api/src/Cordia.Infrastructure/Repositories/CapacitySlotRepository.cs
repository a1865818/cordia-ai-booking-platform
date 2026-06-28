using Cordia.Domain.Capacity;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="ICapacitySlotRepository"/>.</summary>
public class CapacitySlotRepository(ApplicationDbContext db) : ICapacitySlotRepository
{
    public Task<CapacitySlot?> GetAsync(string tenantId, string slotId, CancellationToken ct = default)
        => db.CapacitySlots
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.SlotId == slotId, ct);

    public async Task AddAsync(CapacitySlot slot, CancellationToken ct = default)
    {
        db.CapacitySlots.Add(slot);
        await db.SaveChangesAsync(ct);
    }
}
