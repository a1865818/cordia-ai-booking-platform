using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Observability;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
    ICapacityHoldRepository holds,
    ICapacityLockOrder lockOrder,
    Metrics metrics) : ICapacityReservation
{
    // The ck_capacity_slots_no_overcommit check constraint (held + booked <= total) is the
    // last-line Inv-6 guardrail behind the conditional-write predicate. If it ever fires, the
    // predicate failed to prevent overcommit — surface it as an alert signal (§25.4) and rethrow.
    internal static bool IsOvercommitViolation(Exception ex) =>
        IsCapacityCheckViolation(ex, "no_overcommit");

    /// <summary>
    /// The mirror of <see cref="IsOvercommitViolation"/> for the RELEASE direction:
    /// <c>ck_capacity_slots_nonneg</c> fires when a decrement would drive a counter below zero.
    ///
    /// Unreachable if the idempotency guards hold — a finalized hold is released at most once,
    /// because the flip to <c>Released</c> is itself conditional on <c>Finalized</c>. That is
    /// precisely why it is worth catching: if it ever does fire, the guards have been violated and
    /// the slot's accounting is wrong, which is an Inv-6 alert (§25.4), not an anonymous 500.
    /// </summary>
    internal static bool IsNonNegativeViolation(Exception ex) =>
        IsCapacityCheckViolation(ex, "nonneg");

    private static bool IsCapacityCheckViolation(Exception ex, string constraintFragment) =>
        (ex as PostgresException ?? ex.InnerException as PostgresException) is
        { SqlState: PostgresErrorCodes.CheckViolation, ConstraintName: var name }
        && name is not null
        && name.Contains(constraintFragment, StringComparison.Ordinal);

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
        //
        // ── This writes the SLOT before the HOLD, and that is the documented CLO carve-out. ──
        //
        // Read as "which table does it touch first", this looks like the exact inversion the lock
        // order forbids. It is not, and the distinction is the whole reason the rule is written over
        // ROW LOCKS ON PRE-EXISTING ROWS rather than over table order: step 4 INSERTs a brand-new
        // hold row, and an insert acquires no lock that another transaction could already be
        // holding. It cannot be the second edge of a wait cycle.
        //
        // The only way an insert here waits at all is a conflict on uq_active_hold_per_op_step, and
        // that cannot close a cycle either:
        //   * against a concurrent RELEASE of that hold — if the releaser is uncommitted, step 1
        //     above still reads the hold as Active under READ COMMITTED and returns Held(existing)
        //     without ever touching the slot; if it has committed, the row is no longer in the
        //     partial index, so there is nothing to conflict with.
        //   * against a concurrent RESERVE for the same (operation, step) — both conflict on the
        //     index, but neither ever waits on the other's SLOT lock, so it is a wait, not a cycle
        //     (settled by the transaction abort described at step 4).
        //
        // Do NOT "fix" this by hoisting a lock or reordering the write: the conditional slot UPDATE
        // is the capacity gate, and a hold written before it would have to be unwound when the slot
        // refuses.
        // The capacity predicate IS the guard — no separate version pre-read is needed because
        // the predicate is monotonic (held+booked only ever increases until release) and the
        // WHERE clause is evaluated atomically by the DB engine. rows==1 ⇒ reserved;
        // rows==0 ⇒ slot is full (or slot does not exist for this tenant/slotId).
        //
        // The ck_capacity_slots_no_overcommit check constraint should never fire here because the
        // predicate already rejects an overcommitting write. The narrow catch is a defence-in-depth
        // Inv-6 guardrail: if the constraint DOES fire, emit the capacity-invariant alert signal
        // (§25.4) and rethrow — the real error must not be swallowed.
        int rows;
        try
        {
            rows = await db.CapacitySlots
                .IgnoreQueryFilters()
                .Where(s => s.TenantId == req.TenantId
                            && s.SlotId == req.SlotId
                            && s.TotalCapacity - s.HeldCapacity - s.BookedCapacity >= req.RequestedCapacity)
                .ExecuteUpdateAsync(
                    b => b
                        .SetProperty(s => s.HeldCapacity, s => s.HeldCapacity + req.RequestedCapacity)
                        .SetProperty(s => s.Version, s => s.Version + 1),
                    ct);
        }
        catch (Exception ex) when (IsOvercommitViolation(ex))
        {
            metrics.FulfillmentCapacityInvariantViolationTotal.Add(1);
            throw;
        }

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
        // Ordered by hold id (the repository sorts) so the acquisition below is a total order.
        var activeHolds = await holds.GetActiveByOperationAsync(tenantId, operationId, ct);

        // Take EVERY row lock this method needs before writing any of them (CLO).
        //
        // Releasing hold-by-hold interleaves HOLD(h1) → SLOT(s1) → HOLD(h2) → SLOT(s2), which takes
        // a hold AFTER a slot — the inversion the whole rule exists to forbid — and takes the holds
        // in whatever order the query returned. An operation owns one hold per workflow step, so
        // today the loop almost always runs with a single row and the shape is harmless. It is a
        // public primitive, the next caller will not know that, and the fix is one call.
        await lockOrder.AcquireAsync(
            tenantId,
            holdIds: activeHolds.Select(h => (string?)h.HoldId),
            slotIds: activeHolds.Select(h => h.SlotId),
            ct);

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
    public async Task<FinalizeOutcome> FinalizeAgainstOperationAsync(
        string tenantId,
        string operationId,
        string workflowStep,
        CancellationToken ct = default)
    {
        // Deterministic latest-hold lookup: handles re-reserve-after-expiry correctly.
        var hold = await holds.GetLatestByOperationAndStepAsync(tenantId, operationId, workflowStep, ct);
        if (hold is null)
            return FinalizeOutcome.NotTracked; // never first-party-tracked → caller books normally

        if (hold.Status == HoldStatus.Active)
        {
            // Conditional flip: Active → Finalized, guarded by status='active' (§14.3).
            // Exactly one of {ReleaseAsync(Expired), FinalizeAgainstOperationAsync} wins the
            // single active row; the loser observes rows==0 and does not force.
            var rows = await db.CapacityHolds
                .IgnoreQueryFilters()
                .Where(h => h.TenantId == tenantId && h.HoldId == hold.HoldId && h.Status == HoldStatus.Active)
                .ExecuteUpdateAsync(
                    b => b
                        .SetProperty(h => h.Status, HoldStatus.Finalized)
                        .SetProperty(h => h.ReleaseReason, ReleaseReason.Finalized)
                        .SetProperty(h => h.Version, h => h.Version + 1),
                    ct);

            if (rows == 1)
            {
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

            // rows == 0: raced between read and flip — re-read and decide.
            var fresh = await holds.GetAsync(tenantId, hold.HoldId, ct);
            return fresh?.Status == HoldStatus.Finalized
                ? FinalizeOutcome.Finalized
                : FinalizeOutcome.HoldNoLongerValid;
        }

        // Already Finalized: idempotent retry — do NOT move the slot a second time (FIX 3 + FIX 8).
        if (hold.Status == HoldStatus.Finalized)
            return FinalizeOutcome.Finalized;

        // Expired, Released, or Reconciling → capacity gone (FIX 2).
        return FinalizeOutcome.HoldNoLongerValid;
    }

    /// <inheritdoc/>
    public async Task<bool> AbsorbSameSlotAsync(
        string tenantId,
        string previousOperationId,
        string newOperationId,
        string workflowStep,
        string slotId,
        int totalCapacity,
        CancellationToken ct = default)
    {
        // What this booking contributes to the slot RIGHT NOW, across both operations.
        var previousHold = await holds.GetLatestByOperationAndStepAsync(
            tenantId, previousOperationId, workflowStep, ct);

        var newHold = await holds.GetLatestByOperationAndStepAsync(
            tenantId, newOperationId, workflowStep, ct);

        // ── Take every row lock this method needs BEFORE touching the slot. ──
        //
        // Every other method in this type locks capacity_holds before capacity_slots. This one
        // writes the slot first — deliberately, because a slot that cannot take an increase must
        // leave everything untouched, and that guarantee only holds if the slot is the first thing
        // written. Locking is not writing, so the two requirements are satisfiable together: the
        // locks move to the front, the writes stay where they are.
        //
        // Without this, a same-slot amendment and a concurrent cancel of the same booking take the
        // previous owner's hold and the slot in opposite orders and can deadlock (40P01). Postgres
        // aborts one side and redelivery re-runs it, so nothing is corrupted — but the work is
        // discarded for no reason, and under load the pair can keep losing to each other.
        //
        // Delegated to ICapacityLockOrder rather than ordered inline: it is the one place that
        // knows holds come before slots and that each set is taken ascending by id. (Ordering by
        // role instead of id is how two amendments on adjacent operations — Op2 absorbing Op1, Op3
        // absorbing Op2 — would take the same two rows in opposite orders and rebuild the cycle
        // between themselves.)
        //
        // Both callers already acquire this set, so in practice these locks are re-acquisitions and
        // cost nothing. Kept because this is a public primitive and the next caller may not.
        await lockOrder.AcquireAsync(
            tenantId,
            holdIds: [previousHold?.HoldId, newHold?.HoldId],
            slotIds: [slotId],
            ct);

        // Only FINALIZED holds on THIS slot are booked units. An active/expired/released one is
        // not capacity the booking occupies, so it contributes nothing to the arithmetic.
        var previousUnits = previousHold is { Status: HoldStatus.Finalized } && previousHold.SlotId == slotId
            ? previousHold.ReservedCapacity
            : 0;

        var newUnits = newHold is { Status: HoldStatus.Finalized } && newHold.SlotId == slotId
            ? newHold.ReservedCapacity
            : 0;

        var delta = totalCapacity - (previousUnits + newUnits);

        // Move the slot by the difference, guarded so an INCREASE cannot overcommit. A decrease or
        // a no-op needs no room, and the guard is written so it holds in every direction rather
        // than being skipped for the cases that happen to be safe today.
        if (delta != 0)
        {
            var moved = await db.CapacitySlots
                .IgnoreQueryFilters()
                .Where(s => s.TenantId == tenantId
                            && s.SlotId == slotId
                            && s.TotalCapacity - s.HeldCapacity - s.BookedCapacity >= delta)
                .ExecuteUpdateAsync(
                    b => b
                        .SetProperty(s => s.BookedCapacity, s => s.BookedCapacity + delta)
                        .SetProperty(s => s.Version, s => s.Version + 1),
                    ct);

            // The slot cannot take the increase. Nothing has been written, so the caller still has
            // a coherent booking on its original seat.
            if (moved == 0)
                return false;
        }

        // The previous owner's hold stops being this booking's seat. Guarded on Finalized so a
        // redelivery flips nothing a second time — and because its units are already accounted for
        // in `delta`, this release deliberately does NOT touch the slot.
        if (previousUnits > 0)
        {
            await db.CapacityHolds
                .IgnoreQueryFilters()
                .Where(h => h.TenantId == tenantId
                            && h.HoldId == previousHold!.HoldId
                            && h.Status == HoldStatus.Finalized)
                .ExecuteUpdateAsync(
                    b => b
                        .SetProperty(h => h.Status, HoldStatus.Released)
                        .SetProperty(h => h.ReleaseReason, ReleaseReason.Superseded)
                        .SetProperty(h => h.Version, h => h.Version + 1),
                    ct);
        }

        // The amending operation ends up owning exactly ONE finalized hold, sized to the whole
        // booking — so cancel, compensation and any later amendment all find a single row that
        // describes the seat, which is the property the two-hold shape destroyed.
        if (newHold is not null)
        {
            // Guarded on the status we READ, like every other write in this type. Unconditional,
            // this would finalize a hold that had since expired or been released — quietly turning
            // capacity the slot had already reclaimed back into a booking's seat. It is currently
            // only reachable with an already-Finalized hold, but this is a public primitive and the
            // next caller will not know that.
            await db.CapacityHolds
                .IgnoreQueryFilters()
                .Where(h => h.TenantId == tenantId
                            && h.HoldId == newHold.HoldId
                            && h.Status == newHold.Status)
                .ExecuteUpdateAsync(
                    b => b
                        .SetProperty(h => h.Status, HoldStatus.Finalized)
                        .SetProperty(h => h.ReleaseReason, ReleaseReason.Finalized)
                        .SetProperty(h => h.ReservedCapacity, totalCapacity)
                        .SetProperty(h => h.Version, h => h.Version + 1),
                    ct);
        }
        else
        {
            // Nothing was reserved for the amendment — the same-slot path reserves only what is
            // genuinely new, and an unchanged or shrinking party needs nothing. The booking still
            // has to own its seat under the operation that now holds it.
            var now = DateTimeOffset.UtcNow;
            db.CapacityHolds.Add(new CapacityHold
            {
                TenantId = tenantId,
                HoldId = $"hold_{Guid.NewGuid():N}",
                OperationId = newOperationId,
                WorkflowStep = workflowStep,
                SlotId = slotId,
                ReservedCapacity = totalCapacity,
                Status = HoldStatus.Finalized,
                ReleaseReason = ReleaseReason.Finalized,
                CreatedAt = now,
                ExpiresAt = now,
                Version = 0,
            });
            await db.SaveChangesAsync(ct);
        }

        return true;
    }

    /// <inheritdoc/>
    public async Task<int> ReleaseFinalizedByOperationAsync(
        string tenantId,
        string operationId,
        ReleaseReason reason,
        CancellationToken ct = default)
    {
        // Finalized holds are what a completed booking occupies: the hold row is Finalized and
        // its units live in the slot's BOOKED bucket, not held. Releasing an active hold (the
        // ReleaseByOperationAsync path) is therefore a no-op here and would silently leak the
        // slot after a cancellation.
        // ORDERED, for the same reason ReleaseByOperationAsync's list is: the loop below takes a
        // row lock per hold and then per slot, so an unordered set is an unordered acquisition.
        var holds = await db.CapacityHolds
            .IgnoreQueryFilters()
            .Where(h => h.TenantId == tenantId
                        && h.OperationId == operationId
                        && h.Status == HoldStatus.Finalized)
            .OrderBy(h => h.HoldId)
            .ToListAsync(ct);

        // Every lock up front, holds before slots, each ascending (CLO). Without this the loop
        // interleaves HOLD → SLOT → HOLD → SLOT, taking its second hold after its first slot.
        await lockOrder.AcquireAsync(
            tenantId,
            holdIds: holds.Select(h => (string?)h.HoldId),
            slotIds: holds.Select(h => h.SlotId),
            ct);

        var released = 0;

        foreach (var hold in holds)
        {
            // Conditional flip guarded by status='finalized' — a concurrent or redelivered
            // cancellation observes rows==0 and does not give the same seat back twice (Inv-5).
            var rows = await db.CapacityHolds
                .IgnoreQueryFilters()
                .Where(h => h.TenantId == tenantId
                            && h.HoldId == hold.HoldId
                            && h.Status == HoldStatus.Finalized)
                .ExecuteUpdateAsync(
                    b => b
                        .SetProperty(h => h.Status, HoldStatus.Released)
                        .SetProperty(h => h.ReleaseReason, reason)
                        .SetProperty(h => h.Version, h => h.Version + 1),
                    ct);

            if (rows == 0)
                continue;

            released += rows;

            // Give the units back from BOOKED (not held — finalize already moved them).
            if (hold.SlotId is not null)
            {
                // Defence in depth, matching ReserveAsync's overcommit guard in the other
                // direction: surface a driven-negative counter as the capacity-invariant alert
                // rather than letting it escape as an unclassified 500.
                try
                {
                    await db.CapacitySlots
                        .IgnoreQueryFilters()
                        .Where(s => s.TenantId == tenantId && s.SlotId == hold.SlotId)
                        .ExecuteUpdateAsync(
                            b => b
                                .SetProperty(s => s.BookedCapacity, s => s.BookedCapacity - hold.ReservedCapacity)
                                .SetProperty(s => s.Version, s => s.Version + 1),
                            ct);
                }
                catch (Exception ex) when (IsNonNegativeViolation(ex))
                {
                    metrics.FulfillmentCapacityInvariantViolationTotal.Add(1);
                    throw;
                }
            }
        }

        return released;
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
