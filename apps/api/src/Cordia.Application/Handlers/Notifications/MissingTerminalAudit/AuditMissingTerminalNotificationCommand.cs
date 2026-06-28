using MediatR;

namespace Cordia.Application.Handlers.Notifications.MissingTerminalAudit;

/// <summary>
/// Command: raise an ops work item + audit event for a terminal booking operation that has
/// no accepted/delivered notification (spec 10 §25.4 / FM#12 / Inv-14, T5b).
/// Returns <see langword="true"/> when a new work item was created; <see langword="false"/>
/// when idempotency or the defensive re-check short-circuited (no action needed).
/// </summary>
public sealed record AuditMissingTerminalNotificationCommand(string TenantId, string OperationId)
    : IRequest<bool>;
