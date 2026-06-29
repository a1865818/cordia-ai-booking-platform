using Cordia.Domain.Enums;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Domain seam for auditing a terminal booking operation that is missing an accepted/delivered
/// notification (spec 10 §25.4 / FM#12 / Inv-14, T5b).
///
/// Raises an <c>operations_work_items</c> row + audit event when a terminal operation has no
/// notification in <c>Accepted</c> or <c>Delivered</c> state. Decouples the
/// <c>MissingNotificationAuditHostedService</c> in Infrastructure from MediatR / Application,
/// mirroring <see cref="INotificationReconciler"/> for §12.8 reconciliation.
///
/// Inv-14: this interface MUST NOT mutate <c>booking_operations</c> — the booking stays
/// confirmed; only an ops work item + audit is written.
/// </summary>
public interface IMissingNotificationAuditor
{
    /// <summary>
    /// Raises an ops work item + audit for the given terminal operation if it still has no
    /// accepted/delivered notification of the expected event type for its terminal status.
    /// Idempotent: a no-op if the work item already exists or if a notification was accepted
    /// between the list sweep and this call.
    /// </summary>
    Task AuditAsync(string tenantId, string operationId, OperationStatus terminalStatus, CancellationToken ct = default);
}
