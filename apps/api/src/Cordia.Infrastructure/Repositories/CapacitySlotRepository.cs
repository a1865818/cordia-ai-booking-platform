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

    /// <inheritdoc/>
    public Task LockAsync(string tenantId, string slotId, CancellationToken ct = default)
        // Raw SQL because EF has no `FOR UPDATE`, and a constant rather than the row because the
        // caller wants the LOCK, not the data — exactly as CapacityHoldRepository.LockAsync does.
        //
        // Released when the ambient transaction ends. `tenant_id` is a load-bearing predicate, not
        // decoration: it bounds the lock to this tenant's row (Inv-13). Fully parameterised through
        // FormattableString, so there is no injection surface.
        => db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM public.capacity_slots WHERE tenant_id = {tenantId} AND slot_id = {slotId} FOR UPDATE",
            ct);

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
        // ON CONFLICT DO NOTHING, not catch-the-violation.
        //
        // Catching 23505 cannot work here at all, and this is the trap: the caller runs inside an
        // ambient transaction, and PostgreSQL aborts the WHOLE transaction on a constraint
        // violation. By the time the exception is caught the transaction is already poisoned, so
        // the "lost the race, read the winner back" path — and the COMMIT itself — fail with
        // `current transaction is aborted`. The endpoint documented as idempotent by the derived
        // key therefore 500'd for the loser instead of returning the winner's slot.
        //
        // Letting the database absorb the conflict means no exception is ever raised, so the
        // transaction stays clean and the loser really can read the winner back. This mirrors
        // `IdempotencyRepository.ElectCreatorAsync`, the same primitive for the same reason.
        var rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO public.capacity_slots
                (tenant_id, slot_id, resource_id, starts_at, total_capacity,
                 held_capacity, booked_capacity, version)
            VALUES
                ({slot.TenantId}, {slot.SlotId}, {slot.ResourceId}, {slot.StartsAt},
                 {slot.TotalCapacity}, {slot.HeldCapacity}, {slot.BookedCapacity}, {slot.Version})
            ON CONFLICT (tenant_id, slot_id) DO NOTHING
            """,
            ct);

        return rows == 1;
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
