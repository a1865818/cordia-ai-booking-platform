using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.Capacity.ReleaseExpiredHolds;

/// <summary>
/// Handles <see cref="ReleaseExpiredHoldsCommand"/> inside the
/// <c>InboxDeduplicationBehavior</c> transaction (spec 07 §7, §14.3).
///
/// <para><b>Transaction:</b> this handler runs INSIDE the transaction opened by
/// <c>InboxDeduplicationBehavior</c>. Do NOT open a new <c>IUnitOfWork</c> transaction —
/// all writes share the ambient scoped <c>DbContext</c>.</para>
///
/// <para><b>Routing:</b>
/// <list type="bullet">
///   <item>If <see cref="ReleaseExpiredHoldsCommand.HoldId"/> is non-null → call
///     <c>ICapacityReservation.ReleaseAsync(tenantId, holdId, Expired)</c>. Targets the scheduled
///     hold-expiry job path: one specific hold by ID.</item>
///   <item>If <see cref="ReleaseExpiredHoldsCommand.HoldId"/> is null → call
///     <c>ICapacityReservation.ReleaseByOperationAsync(tenantId, operationId, Expired)</c>. Targets
///     the deposit-expiry path: all active holds for the operation.</item>
/// </list>
/// Both release methods are idempotent — calling them on an already-released / expired / finalized
/// hold is a no-op (rows_affected == 0), safe under duplicate delivery (§14.3, Inv-5).
/// </para>
///
/// <para><b>Op-status transitions:</b> this handler deliberately does NOT force any operation-status
/// transition. Per spec 07 §11, <c>Held</c> has no legal <c>→ Expired</c> edge, and the op-level
/// <c>AwaitingPayment → Expired</c> transition is already owned by <c>ProcessDepositEventCommandHandler</c>
/// (spec 06 §10). Driving an op transition from this handler would either duplicate existing logic or
/// invent an illegal edge. If an op is still in <c>Held</c> when the scheduled TTL fires (rare edge
/// case where the fulfillment driver stalled), the SLA sweeper owns recovery. T7's fail-closed finalize
/// handles a stale-hold op by routing <c>HoldNoLongerValid → Reconciling</c> rather than forcing
/// an op-status here.</para>
/// </summary>
public sealed class ReleaseExpiredHoldsCommandHandler(
    ICapacityReservation capacityReservation,
    IAuditEventRepository auditRepo)
    : IRequestHandler<ReleaseExpiredHoldsCommand, Unit>
{
    public async Task<Unit> Handle(ReleaseExpiredHoldsCommand command, CancellationToken ct)
    {
        var tenantId = command.TenantId;
        var operationId = command.OperationId;

        if (command.HoldId is not null)
        {
            await capacityReservation.ReleaseAsync(tenantId, command.HoldId, ReleaseReason.Expired, ct);
        }
        else
        {
            await capacityReservation.ReleaseByOperationAsync(tenantId, operationId, ReleaseReason.Expired, ct);
        }

        await auditRepo.AddAsync(new AuditEvent
        {
            TenantId = tenantId,
            EventId = $"evt_{Guid.NewGuid():N}",
            EventType = "operation.hold_released",
            Actor = "system",
            SubjectRef = operationId,
            CorrelationId = operationId,
            OperationId = operationId,
            Metadata = JsonSerializer.Serialize(new
            {
                hold_id = command.HoldId,
                reason = "expired",
            }),
            OccurredAt = DateTimeOffset.UtcNow,
        }, ct);

        return Unit.Value;
    }
}
