using Cordia.Domain.Entities;
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

    public async Task<IReadOnlyList<CapacitySlot>> ListAsync(
        string tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
        => await db.CapacitySlots
            .AsNoTracking()
            // Explicit tenant predicate in ADDITION to the ambient global query filter, the way
            // GetAsync and NotificationRecipientResolver do it: a mis-scoped or unset
            // ITenantContext (background scope, test host, future batch job) must never return
            // another venue's capacity (Inv-13).
            .Where(s => s.TenantId == tenantId && s.StartsAt >= from && s.StartsAt < to)
            .OrderBy(s => s.StartsAt)
            .ThenBy(s => s.SlotId)
            .ToListAsync(ct);
}
