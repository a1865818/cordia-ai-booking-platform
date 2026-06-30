using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Observability;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Capacity.ReleaseExpiredHolds;

/// <summary>
/// Handles <see cref="ReleaseExpiredHoldsCommand"/> inside the
/// <c>InboxDeduplicationBehavior</c> transaction (spec 07 §7, §14.3).
///
/// <para><b>Transaction:</b> this handler runs INSIDE the transaction opened by
/// <c>InboxDeduplicationBehavior</c>. Do NOT open a new <c>IUnitOfWork</c> transaction —
/// all writes share the ambient scoped <c>DbContext</c>.</para>
///
/// <para><b>Routing (FIX 5 — fail-closed on malformed payload):</b>
/// <list type="bullet">
///   <item><see cref="ReleaseScope.Hold"/> (<c>booking.scheduled.hold_expiry.v1</c>):
///     if <see cref="ReleaseExpiredHoldsCommand.HoldId"/> is null or empty, log a warning and return
///     Unit WITHOUT releasing anything (fail closed — prevents accidental bulk release on a malformed
///     scheduled-expiry payload). Only when HoldId is present: call
///     <c>ICapacityReservation.ReleaseAsync(tenantId, holdId, Expired)</c>.</item>
///   <item><see cref="ReleaseScope.Operation"/> (<c>capacity.hold_release.requested.v1</c>):
///     call <c>ICapacityReservation.ReleaseByOperationAsync(tenantId, operationId, Expired)</c>.
///     This bulk-release-by-op is intentional for the deposit-expiry seam.</item>
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
/// handles a stale-hold op by routing <c>HoldNoLongerValid → CompensationRequired</c> rather than
/// forcing an op-status here.</para>
/// </summary>
public sealed class ReleaseExpiredHoldsCommandHandler(
    ICapacityReservation capacityReservation,
    IAuditEventRepository auditRepo,
    ILogger<ReleaseExpiredHoldsCommandHandler> logger,
    Metrics metrics)
    : IRequestHandler<ReleaseExpiredHoldsCommand, Unit>
{
    public async Task<Unit> Handle(ReleaseExpiredHoldsCommand command, CancellationToken ct)
    {
        var tenantId = command.TenantId;
        var operationId = command.OperationId;

        if (command.Scope == ReleaseScope.Hold)
        {
            // FIX 5: fail CLOSED — a booking.scheduled.hold_expiry.v1 message with a missing
            // hold_id must never trigger a bulk release. Log a warning and return Unit (no-op).
            if (string.IsNullOrEmpty(command.HoldId))
            {
                logger.LogWarning(
                    "Hold expiry message for operation {OperationId} (tenant {TenantId}) is missing hold_id — ignoring to prevent accidental bulk release.",
                    operationId, tenantId);
                return Unit.Value;
            }

            await capacityReservation.ReleaseAsync(tenantId, command.HoldId, ReleaseReason.Expired, ct);
            metrics.FulfillmentHoldExpiredTotal.Add(1);
        }
        else
        {
            // ReleaseScope.Operation: bulk-release-by-op (deposit-expiry path — intentional).
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
