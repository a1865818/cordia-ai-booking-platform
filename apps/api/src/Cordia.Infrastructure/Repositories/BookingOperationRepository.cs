using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.EntityFramework.Concurrency;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IBookingOperationRepository"/>.</summary>
public class BookingOperationRepository(ApplicationDbContext db) : IBookingOperationRepository
{
    // Terminal statuses that block the cancellation flag from being set (spec 06 §15.5, FM#13).
    // Post-booking cancellation (Booked) is a separate operation (spec 08) and is intentionally
    // included here so that a Booked op cannot be flagged via this path.
    //
    // MATERIALISED from the state machine, not retyped. An array is genuinely required — EF Core
    // translates `array.Contains(column)` into SQL `= ANY`, and cannot translate
    // OperationStateMachine.IsTerminal — but "we need a different shape" was never a reason to keep
    // a second copy of the CONTENTS. Spec 06 owns which statuses are terminal; this is that set in
    // the shape the query planner needs.
    private static readonly OperationStatus[] TerminalStatuses =
        [.. OperationStateMachine.TerminalStates];

    public async Task AddAsync(BookingOperation operation, CancellationToken ct = default)
    {
        db.BookingOperations.Add(operation);
        await db.SaveChangesAsync(ct);
    }

    public Task<BookingOperation?> GetAsync(string operationId, CancellationToken ct = default)
        => db.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OperationId == operationId, ct);

    /// <summary>
    /// Version-guarded status transition. <b>Every edge is validated against
    /// <see cref="OperationStateMachine"/> before it can reach the database.</b>
    ///
    /// <para>This is the ONE write path for <c>booking_operations.status</c>, and until now it did
    /// not consult the state machine at all: whether an edge was legal depended on the caller
    /// choosing to call <c>EnsureAllowed</c> first, and nine of them did not. The catalog, waitlist
    /// and pre-order domains were disciplined about it; the booking-operation domain mostly was not.
    /// The consequence is silent rather than loud — remove an edge from the §11 map and those nine
    /// call sites keep writing the now-illegal transition, so the authority and the actual behaviour
    /// diverge with nothing failing.</para>
    ///
    /// <para>Validating here makes the map load-bearing instead of advisory, and it cannot be
    /// bypassed by forgetting. <see cref="InvalidStateTransitionException"/> is not a new failure
    /// mode for the driver: <c>AdvanceBookingOperationCommandHandler</c> already catches it
    /// alongside <c>PoisonMessageException</c>, persists an open work item and the "still confirming"
    /// notification, and dead-letters the transport message — the operation stays in its recoverable
    /// business state (Inv-14) and an operator is told. An illegal edge is a defect that will never
    /// self-heal, which is exactly what that path is for.</para>
    ///
    /// <para>Validation is on the edge alone. Whether the row is actually AT
    /// <paramref name="expectedStatus"/> stays the database's job via the guarded predicate, which
    /// returns 0 rows on a lost race — a legal edge that loses a race is not an error.</para>
    /// </summary>
    public Task<int> TryTransitionStatusAsync(
        string tenantId,
        string operationId,
        long expectedVersion,
        OperationStatus expectedStatus,
        OperationStatus next,
        string? reason = null,
        string? bookingId = null,
        CancellationToken ct = default)
    {
        OperationStateMachine.EnsureAllowed(expectedStatus, next);

        return bookingId is not null
            ? VersionTokenUpdate.UpdateAsync<BookingOperation>(
                db,
                expectedVersion,
                e => e.TenantId == tenantId && e.OperationId == operationId && e.Status == expectedStatus,
                s => s
                    .SetProperty(x => x.Status, next)
                    .SetProperty(x => x.StatusReason, reason)
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow)
                    .SetProperty(x => x.BookingId, bookingId),
                ct)
            : VersionTokenUpdate.UpdateAsync<BookingOperation>(
                db,
                expectedVersion,
                e => e.TenantId == tenantId && e.OperationId == operationId && e.Status == expectedStatus,
                s => s
                    .SetProperty(x => x.Status, next)
                    .SetProperty(x => x.StatusReason, reason)
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow),
                ct);
    }

    public Task<int> TrySetBookingIdAsync(
        string tenantId,
        string operationId,
        string bookingId,
        CancellationToken ct = default)
        => db.BookingOperations
            .IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId
                        && o.OperationId == operationId
                        && o.BookingId == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.BookingId, bookingId)
                       .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow),
                ct);

    public Task<int> SetCancellationRequestedAsync(
        string tenantId,
        string operationId,
        DateTimeOffset when,
        CancellationToken ct = default)
        => db.BookingOperations
            .IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId
                        && o.OperationId == operationId
                        && o.CancellationRequestedAt == null
                        && !TerminalStatuses.Contains(o.Status))
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.CancellationRequestedAt, when)
                       .SetProperty(x => x.UpdatedAt, when),
                ct);

    // Statuses eligible for SLA evaluation (IgnoreQueryFilters — cross-tenant), MATERIALISED from
    // the state machine for the same reason as TerminalStatuses above.
    //
    // This list and EvaluateOperationSlaCommand's `UserWaitStatuses` used to be maintained
    // independently, in different projects, as complements of each other — so a new status could
    // silently land in both partitions or in neither. Both now derive from the one definition, and
    // WorkflowDefinitionTests asserts the partition covers every status exactly once.
    private static readonly OperationStatus[] SlaEligibleStatuses =
        [.. OperationStateMachine.SlaEligibleStates];

    public Task<BookingOperation?> GetBookedOperationForBookingAsync(
        string tenantId,
        string bookingId,
        CancellationToken ct = default)
        // The operation that actually created the booking — i.e. the one whose capacity hold the
        // booking occupies. Cancel/modify operations also carry this BookingId, so they are
        // excluded by type; the newest Booked one wins if a booking was ever re-created.
        => db.BookingOperations
            .AsNoTracking()
            .Where(o => o.TenantId == tenantId
                        && o.BookingId == bookingId
                        && o.Status == OperationStatus.Booked
                        && (o.OperationType == OperationTypeEnum.Create
                            || o.OperationType == OperationTypeEnum.Reschedule
                            || o.OperationType == OperationTypeEnum.Modify))
            .OrderByDescending(o => o.UpdatedAt)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<BookingOperation>> ListNonTerminalForSlaAsync(
        DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        var results = await db.BookingOperations
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(o => SlaEligibleStatuses.Contains(o.Status))
            .OrderBy(o => o.UpdatedAt)
            .Take(limit)
            .ToListAsync(ct);
        return results;
    }
}
