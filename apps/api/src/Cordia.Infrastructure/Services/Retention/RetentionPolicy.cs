using Cordia.Domain.Enums;

namespace Cordia.Infrastructure.Services.Retention;

/// <summary>
/// Pure, stateless policy helper for idempotency-record retention decisions (spec 12 §6.3, Inv-14).
/// No DB access — unit-testable in isolation.
/// </summary>
/// <remarks>
/// This sweeper operates on the stored <c>retention_deadline</c> column in
/// <c>idempotency_records</c> and the open-work/dispute guard derived from the
/// linked <c>booking_operations.status</c>.  The concrete deadline formula
/// (booking-life + dispute/audit window) and jurisdiction retention for transcripts
/// and verification grants remain open decisions (spec 12 §13); this helper is
/// intentionally extensible to those tables once those decisions land.
/// </remarks>
public static class RetentionPolicy
{
    /// <summary>
    /// Terminal operation statuses: no open work or dispute window → safe to prune (Inv-14).
    /// </summary>
    private static readonly HashSet<OperationStatus> Terminal = new()
    {
        OperationStatus.Booked,
        OperationStatus.Rejected,
        OperationStatus.Expired,
        OperationStatus.FailedFinal,
    };

    /// <summary>
    /// Returns <c>true</c> when the record is eligible for physical deletion:
    /// the retention window has elapsed AND there is no open work/dispute window.
    /// </summary>
    /// <param name="retentionDeadline">The stored deadline from the record.</param>
    /// <param name="now">The reference "now" clock value (injected for testability).</param>
    /// <param name="linkedStatus">
    /// Status of the linked <c>booking_operations</c> row, or <c>null</c> if no linked
    /// operation exists (orphan) — orphans are prunable once past their deadline.
    /// </param>
    public static bool ShouldPrune(
        DateTimeOffset retentionDeadline,
        DateTimeOffset now,
        OperationStatus? linkedStatus)
        => retentionDeadline < now
           && (linkedStatus is null || Terminal.Contains(linkedStatus.Value));

    /// <summary>Returns <c>true</c> when <paramref name="status"/> is a terminal operation status.</summary>
    public static bool IsTerminal(OperationStatus status) => Terminal.Contains(status);
}
