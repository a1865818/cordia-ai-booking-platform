using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Reporting;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IBookingFunnelReadModel"/> (spec 13 §3, §5.3, Inv-16).
///
/// Tenant scoping: the global query filter on <see cref="ApplicationDbContext"/> already filters
/// by <see cref="ITenantContext.TenantId"/>.  This read model receives an explicit
/// <paramref name="tenantId"/> parameter and passes it through
/// <c>IgnoreQueryFilters() + explicit WHERE</c> so the query is self-contained and testable
/// without coupling to a live <see cref="ITenantContext"/> (consistent with how other read repos
/// handle cross-tenant or tenant-parameterised reads, e.g. <see cref="BookingOperationRepository"/>).
///
/// All queries use <c>AsNoTracking()</c> (read-only, Inv-16).
/// </summary>
public class BookingFunnelReadModel(ApplicationDbContext db) : IBookingFunnelReadModel
{
    // Terminal-failure statuses (conservative: only states whose name clearly denotes a
    // customer-visible failure outcome, per spec 13 §5.3 and the OperationStatus enum).
    // Booked is NOT a failure.  Reconciling is ambiguous, not terminal.
    // Waitlisted and AlternativesRequired are informational terminals, not "failures" in the
    // product-reporting sense — excluded to stay conservative (spec says "terminal failure").
    private static readonly OperationStatus[] TerminalFailureStatuses =
    [
        OperationStatus.Rejected,
        OperationStatus.FailedFinal,
        OperationStatus.Expired,
    ];

    /// <inheritdoc/>
    public async Task<BookingFunnel> GetFunnelAsync(
        string tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        // --- accepted: every booking_operations row in the window for this tenant ---
        // IgnoreQueryFilters so the explicit tenantId filter owns the scope; consistent
        // with how other parameterised reads bypass the ambient global filter.
        var accepted = await db.BookingOperations
            .AsNoTracking()
            .IgnoreQueryFilters()
            .CountAsync(
                o => o.TenantId == tenantId
                     && o.CreatedAt >= from
                     && o.CreatedAt < to,
                ct);

        // --- confirmed: Status == Booked in the window ---
        // A cancellation also terminates at `Booked` (D1 has no cancelled operation status), so
        // it must be excluded or cancelling a booking would RAISE the confirmed figure.
        var confirmed = await db.BookingOperations
            .AsNoTracking()
            .IgnoreQueryFilters()
            .CountAsync(
                o => o.TenantId == tenantId
                     && o.CreatedAt >= from
                     && o.CreatedAt < to
                     && o.Status == OperationStatus.Booked
                     && o.OperationType != OperationTypeEnum.Cancel,
                ct);

        // --- notified: Booked operations with a Delivered notification ---
        // Single join query — avoids N+1.  Notifications are linked to operations by OperationId.
        // We count distinct operations (not notifications) so a Booked op with multiple Delivered
        // notifications is still counted once.
        var notified = await db.BookingOperations
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId
                        && o.CreatedAt >= from
                        && o.CreatedAt < to
                        && o.Status == OperationStatus.Booked
                        && o.OperationType != OperationTypeEnum.Cancel)
            .Join(
                db.Notifications
                    .AsNoTracking()
                    .IgnoreQueryFilters()
                    .Where(n => n.TenantId == tenantId && n.State == NotificationState.Delivered),
                op => op.OperationId,
                n => n.OperationId,
                (op, n) => op.OperationId)
            .Distinct()
            .CountAsync(ct);

        // --- failedTerminal: terminal-failure statuses in the window ---
        var failedTerminal = await db.BookingOperations
            .AsNoTracking()
            .IgnoreQueryFilters()
            .CountAsync(
                o => o.TenantId == tenantId
                     && o.CreatedAt >= from
                     && o.CreatedAt < to
                     && TerminalFailureStatuses.Contains(o.Status),
                ct);

        // --- ambiguous: Status == Reconciling (provider outcome unknown, §5.3) ---
        var ambiguous = await db.BookingOperations
            .AsNoTracking()
            .IgnoreQueryFilters()
            .CountAsync(
                o => o.TenantId == tenantId
                     && o.CreatedAt >= from
                     && o.CreatedAt < to
                     && o.Status == OperationStatus.Reconciling,
                ct);

        // notifyPending is derived — never read separately, always computed from confirmed − notified.
        var notifyPending = confirmed - notified;

        return new BookingFunnel(
            TenantId: tenantId,
            From: from,
            To: to,
            Accepted: accepted,
            Confirmed: confirmed,
            Notified: notified,
            FailedTerminal: failedTerminal,
            Ambiguous: ambiguous,
            NotifyPending: notifyPending);
    }
}
