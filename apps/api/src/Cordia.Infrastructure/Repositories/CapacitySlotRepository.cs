using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

    /// <summary>
    /// Inserts the slot, or reports that someone else already did. Returns <c>false</c> on a
    /// primary-key conflict rather than throwing.
    ///
    /// <para>The declaration endpoint promises idempotency by the derived key, but a pre-read
    /// followed by an insert is not atomic: two operators declaring the same slot — or one
    /// double-clicking — both pass the read, and the loser's insert violated
    /// <c>pk_capacity_slots</c> and surfaced as a 500. The caller re-reads the winner and returns
    /// it, which is what the endpoint said it would do all along.</para>
    /// </summary>
    public async Task<bool> TryAddAsync(CapacitySlot slot, CancellationToken ct = default)
    {
        db.CapacitySlots.Add(slot);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        // ONLY the unique-violation (23505) is "someone else won". Catching every
        // DbUpdateException reported a genuinely failed insert — a check/not-null violation, a
        // serialization failure, a dropped connection — as a lost race, and the caller then
        // re-read a winner that does not exist and returned its own unsaved instance as a
        // successful declaration: a 200 for a slot nothing persisted, no audit row (that write is
        // gated on winning), and every booking against it silently taking the untracked path.
        // Anything else must surface.
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Detach so the failed insert cannot be retried by a later SaveChanges on this context.
            db.Entry(slot).State = EntityState.Detached;
            return false;
        }
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
