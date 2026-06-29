using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
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
    private static readonly OperationStatus[] TerminalStatuses =
    [
        OperationStatus.Booked,
        OperationStatus.Rejected,
        OperationStatus.Expired,
        OperationStatus.Waitlisted,
        OperationStatus.FailedFinal,
    ];

    public async Task AddAsync(BookingOperation operation, CancellationToken ct = default)
    {
        db.BookingOperations.Add(operation);
        await db.SaveChangesAsync(ct);
    }

    public Task<BookingOperation?> GetAsync(string operationId, CancellationToken ct = default)
        => db.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OperationId == operationId, ct);

    public Task<int> TryTransitionStatusAsync(
        string tenantId,
        string operationId,
        long expectedVersion,
        OperationStatus expectedStatus,
        OperationStatus next,
        string? reason = null,
        string? bookingId = null,
        CancellationToken ct = default)
        => bookingId is not null
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

    // Non-terminal automated statuses eligible for SLA evaluation (IgnoreQueryFilters — cross-tenant).
    // Explicitly listed because OperationStateMachine.IsTerminal is not translatable to SQL.
    private static readonly OperationStatus[] SlaEligibleStatuses =
    [
        OperationStatus.Accepted,
        OperationStatus.Validating,
        OperationStatus.ReservingCapacity,
        OperationStatus.Held,
        OperationStatus.Finalizing,
        OperationStatus.Reconciling,
        OperationStatus.CompensationRequired,
        OperationStatus.ManualReview,
    ];

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
