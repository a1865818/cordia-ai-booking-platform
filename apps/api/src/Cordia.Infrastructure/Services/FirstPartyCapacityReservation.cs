using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Services;

/// <summary>
/// First-party implementation of <see cref="ICapacityReservation"/> that performs atomic
/// conditional writes against <c>capacity_slots</c> and manages the <c>capacity_holds</c>
/// lifecycle (spec 07 §4.3, §4.4, Inv-6).
/// <para>
/// This service must be called inside an ambient EF Core transaction (via
/// <c>IUnitOfWork.ExecuteInTransactionAsync</c>). It shares the scoped
/// <see cref="ApplicationDbContext"/> with the caller and MUST NOT open its own transaction.
/// All <c>ExecuteUpdateAsync</c> and <c>SaveChangesAsync</c> calls participate in the
/// caller's ambient transaction, so the slot write, hold insert, and operation status
/// transition all commit atomically (Inv-4).
/// </para>
/// </summary>
public sealed class FirstPartyCapacityReservation(
    ApplicationDbContext db,
    ICapacityHoldRepository holds) : ICapacityReservation
{
    /// <inheritdoc/>
    public async Task<ReservationOutcome> ReserveAsync(ReserveRequest req, CancellationToken ct = default)
    {
        // Step 1 — Idempotency pre-check (BEFORE any slot write).
        // If an active hold already exists for (tenant, operation, workflow_step), return it
        // without touching the slot. This is the normal duplicate-delivery / re-drive path
        // and prevents double-incrementing held_capacity (Inv-5, §27.2).
        var existingHolds = await holds.GetActiveByOperationAsync(req.TenantId, req.OperationId, ct);
        var existing = existingHolds.FirstOrDefault(h => h.WorkflowStep == req.WorkflowStep);
        if (existing is not null)
            return ReservationOutcome.Held(existing);

        // Step 2 — Atomic conditional increment (§4.3, Inv-6).
        // The capacity predicate IS the guard — no separate version pre-read is needed because
        // the predicate is monotonic (held+booked only ever increases until release) and the
        // WHERE clause is evaluated atomically by the DB engine. rows==1 ⇒ reserved;
        // rows==0 ⇒ slot is full (or slot does not exist for this tenant/slotId).
        var rows = await db.CapacitySlots
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == req.TenantId
                        && s.SlotId == req.SlotId
                        && s.TotalCapacity - s.HeldCapacity - s.BookedCapacity >= req.RequestedCapacity)
            .ExecuteUpdateAsync(
                b => b
                    .SetProperty(s => s.HeldCapacity, s => s.HeldCapacity + req.RequestedCapacity)
                    .SetProperty(s => s.Version, s => s.Version + 1),
                ct);

        // Step 3 — Slot full (or slot row does not exist under this predicate).
        if (rows == 0)
            return ReservationOutcome.NoCapacity();

        // Step 4 — rows == 1: insert the active hold.
        // Do NOT catch/swallow DbUpdateException from the hold insert. If the
        // uq_active_hold_per_op_step partial unique index fires (race between two concurrent
        // re-drives that both passed the pre-check), PostgreSQL aborts the ambient transaction
        // and no further commands can run in it. The caller's ExecuteInTransactionAsync rolls
        // back — which also undoes the slot increment — and the driver re-drives. On the next
        // re-drive the pre-check (step 1) will find the now-committed active hold and return
        // Held(existing) without touching the slot.
        var hold = new CapacityHold
        {
            TenantId = req.TenantId,
            HoldId = $"hold_{Guid.NewGuid():N}",
            OperationId = req.OperationId,
            WorkflowStep = req.WorkflowStep,
            SlotId = req.SlotId,
            ReservedCapacity = req.RequestedCapacity,
            Status = HoldStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = req.ExpiresAt,
            Version = 0,
        };

        await holds.AddAsync(hold, ct);
        return ReservationOutcome.Held(hold);
    }

    /// <inheritdoc/>
    public async Task ReleaseAsync(string tenantId, string holdId, ReleaseReason reason, CancellationToken ct = default)
    {
        // Read the hold first to get ReservedCapacity + SlotId for the slot decrement.
        // If the hold doesn't exist, this is a no-op (idempotent).
        var hold = await holds.GetAsync(tenantId, holdId, ct);
        if (hold is null)
            return;

        await ReleaseHoldAsync(tenantId, hold, reason, ct);
    }

    /// <inheritdoc/>
    public async Task<int> ReleaseByOperationAsync(
        string tenantId,
        string operationId,
        ReleaseReason reason,
        CancellationToken ct = default)
    {
        var activeHolds = await holds.GetActiveByOperationAsync(tenantId, operationId, ct);

        var released = 0;
        foreach (var hold in activeHolds)
        {
            var rows = await ReleaseHoldAsync(tenantId, hold, reason, ct);
            if (rows == 1)
                released++;
        }

        return released;
    }

    /// <inheritdoc/>
    public async Task<FinalizeOutcome> FinalizeAgainstHoldAsync(
        string tenantId,
        string holdId,
        CancellationToken ct = default)
    {
        // Read the hold to get ReservedCapacity + SlotId for the slot update.
        var hold = await holds.GetAsync(tenantId, holdId, ct);
        if (hold is null)
            return FinalizeOutcome.HoldNoLongerValid;

        // Conditional flip: Active → Finalized, guarded by status='active' (§14.3).
        // Exactly one of {ReleaseAsync(Expired), FinalizeAgainstHoldAsync} wins the single
        // active row; the loser observes rows==0 and does not force (§14.3 race resolution).
        var rows = await db.CapacityHolds
            .IgnoreQueryFilters()
            .Where(h => h.TenantId == tenantId && h.HoldId == holdId && h.Status == HoldStatus.Active)
            .ExecuteUpdateAsync(
                b => b
                    .SetProperty(h => h.Status, HoldStatus.Finalized)
                    .SetProperty(h => h.ReleaseReason, ReleaseReason.Finalized)
                    .SetProperty(h => h.Version, h => h.Version + 1),
                ct);

        if (rows == 0)
            return FinalizeOutcome.HoldNoLongerValid;

        // rows == 1: move held → booked on the first-party slot (only when SlotId is set).
        if (hold.SlotId is not null)
        {
            await db.CapacitySlots
                .IgnoreQueryFilters()
                .Where(s => s.TenantId == tenantId && s.SlotId == hold.SlotId)
                .ExecuteUpdateAsync(
                    b => b
                        .SetProperty(s => s.HeldCapacity, s => s.HeldCapacity - hold.ReservedCapacity)
                        .SetProperty(s => s.BookedCapacity, s => s.BookedCapacity + hold.ReservedCapacity)
                        .SetProperty(s => s.Version, s => s.Version + 1),
                    ct);
        }

        return FinalizeOutcome.Finalized;
    }

    /// <summary>
    /// Shared release logic used by both <see cref="ReleaseAsync"/> and
    /// <see cref="ReleaseByOperationAsync"/>. Conditionally flips the hold from Active to the
    /// target status and, on success, decrements the slot's held capacity.
    /// Returns 1 if the hold was released, 0 if it was already non-Active (idempotent no-op).
    /// </summary>
    private async Task<int> ReleaseHoldAsync(
        string tenantId,
        CapacityHold hold,
        ReleaseReason reason,
        CancellationToken ct)
    {
        // Map release reason → target hold status.
        var targetStatus = reason switch
        {
            ReleaseReason.Expired => HoldStatus.Expired,
            _ => HoldStatus.Released, // Compensated, Superseded, ProviderReleased, and defensively Finalized
        };

        // Conditional status flip guarded by status='active' (§14.3, Inv-5).
        var rows = await db.CapacityHolds
            .IgnoreQueryFilters()
            .Where(h => h.TenantId == tenantId && h.HoldId == hold.HoldId && h.Status == HoldStatus.Active)
            .ExecuteUpdateAsync(
                b => b
                    .SetProperty(h => h.Status, targetStatus)
                    .SetProperty(h => h.ReleaseReason, reason)
                    .SetProperty(h => h.Version, h => h.Version + 1),
                ct);

        // rows == 0 → hold was already released/expired/finalized — idempotent no-op.
        if (rows == 0)
            return 0;

        // rows == 1 → decrement slot held capacity (only for first-party holds with a slot).
        if (hold.SlotId is not null)
        {
            await db.CapacitySlots
                .IgnoreQueryFilters()
                .Where(s => s.TenantId == tenantId && s.SlotId == hold.SlotId)
                .ExecuteUpdateAsync(
                    b => b
                        .SetProperty(s => s.HeldCapacity, s => s.HeldCapacity - hold.ReservedCapacity)
                        .SetProperty(s => s.Version, s => s.Version + 1),
                    ct);
        }

        return 1;
    }
}
