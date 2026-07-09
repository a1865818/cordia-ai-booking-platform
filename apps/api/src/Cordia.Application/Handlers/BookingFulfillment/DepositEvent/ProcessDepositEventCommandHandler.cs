using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.DepositEvent;

/// <summary>
/// Handles deposit lifecycle events inside the InboxDeduplicationBehavior transaction
/// (spec 06 §10 deposit-event table).
///
/// IMPORTANT: this handler runs INSIDE the transaction opened by InboxDeduplicationBehavior.
/// Do NOT open a new IUnitOfWork transaction — all writes share the ambient scoped DbContext
/// (repositories use SaveChangesAsync internally, which participates in the same ambient txn
/// via EF Core's transaction enlistment).
///
/// Guards: if op.Status != AwaitingPayment → return Unit (wrong state; behavior commits
/// the inbox row as a no-op so the message is acked without double-processing).
/// All state transitions are version-guarded; if rows==0 → skip dependent writes.
/// </summary>
public sealed class ProcessDepositEventCommandHandler(
    IBookingOperationRepository opRepo,
    IDepositSessionRepository depositSessionRepo,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo)
    : IRequestHandler<ProcessDepositEventCommand, Unit>
{
    public async Task<Unit> Handle(ProcessDepositEventCommand command, CancellationToken ct)
    {
        var tenantId = command.TenantId;
        var operationId = command.OperationId;

        var op = await opRepo.GetAsync(operationId, ct);
        if (op is null || op.TenantId != tenantId || op.Status != OperationStatus.AwaitingPayment)
            return Unit.Value; // wrong state or already handled — inbox row is committed as no-op

        var now = DateTimeOffset.UtcNow;

        if (command.Kind == DepositEventKind.Paid)
        {
            // AwaitingPayment → Finalizing (version-guarded).
            var rows = await opRepo.TryTransitionStatusAsync(
                tenantId, operationId, op.Version,
                OperationStatus.AwaitingPayment, OperationStatus.Finalizing,
                reason: "deposit_paid", ct: ct);

            if (rows == 0)
                return Unit.Value; // lost race — inbox row committed, no other writes

            // Transition the deposit session → Paid.
            var session = await depositSessionRepo.GetByOperationAsync(tenantId, operationId, ct);
            if (session is not null)
            {
                await depositSessionRepo.TryTransitionStatusAsync(
                    tenantId, session.DepositSessionId, session.Version,
                    session.Status, DepositStatus.Paid,
                    paidAt: now, ct: ct);
            }

            // Emit booking.command.requested.v1 continuation so the driver finalizes on the next tick.
            await outboxRepo.AddAsync(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = $"obx_{Guid.NewGuid():N}",
                OperationId = operationId,
                MessageType = "booking.command.requested.v1",
                CorrelationId = operationId,
                Payload = JsonSerializer.Serialize(new
                {
                    operation_id = operationId,
                    tenant_id = tenantId,
                }),
                OccurredAt = now,
            }, ct);

            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = tenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "operation.deposit_paid",
                Actor = "system",
                SubjectRef = operationId,
                CorrelationId = operationId,
                OperationId = operationId,
                Metadata = JsonSerializer.Serialize(new { reason = "deposit_paid" }),
                OccurredAt = now,
            }, ct);
        }
        else
        {
            // DepositEventKind.Expired: AwaitingPayment → Expired (version-guarded).
            var rows = await opRepo.TryTransitionStatusAsync(
                tenantId, operationId, op.Version,
                OperationStatus.AwaitingPayment, OperationStatus.Expired,
                reason: "deposit_expired", ct: ct);

            if (rows == 0)
                return Unit.Value; // lost race — no other writes

            // Transition deposit session → Expired.
            var session = await depositSessionRepo.GetByOperationAsync(tenantId, operationId, ct);
            if (session is not null)
            {
                await depositSessionRepo.TryTransitionStatusAsync(
                    tenantId, session.DepositSessionId, session.Version,
                    session.Status, DepositStatus.Expired, ct: ct);
            }

            // Emit capacity.hold_release.requested.v1 (spec 07 releases the hold).
            await outboxRepo.AddAsync(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = $"obx_{Guid.NewGuid():N}",
                OperationId = operationId,
                MessageType = "capacity.hold_release.requested.v1",
                CorrelationId = operationId,
                Payload = JsonSerializer.Serialize(new
                {
                    operation_id = operationId,
                    tenant_id = tenantId,
                }),
                OccurredAt = now,
            }, ct);

            // Spec 11 §5.3 / Inv-8: a pre-order operation signals the saga (booking_failed, reason
            // deposit_expired) so it compensates the reserved items and emits the ONE combined
            // outcome — a direct customer notice here would be a premature first outcome that the
            // saga's later booking_failed compensation would duplicate. A normal operation keeps
            // the customer-facing deposit-expired notification.
            await outboxRepo.AddAsync(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = $"obx_{Guid.NewGuid():N}",
                OperationId = operationId,
                MessageType = op.IsPreorder
                    ? Cordia.Application.Handlers.Preorder.PreorderSagaMessages.BookingFailed
                    : "notification.deposit_expired.v1",
                CorrelationId = operationId,
                Payload = JsonSerializer.Serialize(new
                {
                    operation_id = operationId,
                    tenant_id = tenantId,
                    reason = "deposit_expired",
                }),
                OccurredAt = now,
            }, ct);

            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = tenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "operation.expired",
                Actor = "system",
                SubjectRef = operationId,
                CorrelationId = operationId,
                OperationId = operationId,
                Metadata = JsonSerializer.Serialize(new { reason = "deposit_expired" }),
                OccurredAt = now,
            }, ct);
        }

        return Unit.Value;
    }
}
