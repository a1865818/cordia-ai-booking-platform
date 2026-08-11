using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.Sla;

/// <summary>
/// Evaluates SLA thresholds for a single booking operation (spec 06 §8 / §17.5, T9).
///
/// Emits:
/// - <c>notification.still_confirming.v1</c> when elapsed >= pending threshold (user-facing "we're still working on it").
/// - <c>notification.sla_escalation.v1</c> + <c>operations_work_item</c> (State="open") when elapsed >= escalation threshold.
///
/// Idempotency: deterministic message_ids + open-work-item existence check prevent duplicates on repeated evaluation.
/// Does NOT transition OperationStatus — escalation is surfaced as a work item, not a forced status change.
/// </summary>
public sealed record EvaluateOperationSlaCommand(string TenantId, string OperationId) : IRequest<Unit>;

/// <summary>Handler for <see cref="EvaluateOperationSlaCommand"/>.</summary>
public sealed class EvaluateOperationSlaCommandHandler(
    IBookingOperationRepository opRepo,
    IOperationWorkItemRepository workItemRepo,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    IUnitOfWork uow) : IRequestHandler<EvaluateOperationSlaCommand, Unit>
{
    // §8 SLA thresholds (seconds)
    private const int ReconcilePendingSeconds = 0;
    private const int ReconcileEscalationSeconds = 900;
    private const int CompensationEscalationSeconds = 1800;
    private const int InProgressPendingSeconds = 30;
    private const int InProgressEscalationSeconds = 600;
    private const int DefaultOwnerTargetSeconds = 3600;

    // User-wait states — waiting on the customer, NOT an SLA breach.
    //
    // Asked of the state machine rather than listed here. This set and
    // BookingOperationRepository.SlaEligibleStatuses are two halves of one partition of the same 16
    // statuses, and they were maintained in separate projects with nothing tying them together.
    private static readonly IReadOnlySet<OperationStatus> UserWaitStatuses =
        OperationStateMachine.UserWaitStates;

    public async Task<Unit> Handle(EvaluateOperationSlaCommand request, CancellationToken cancellationToken)
    {
        // Load op (no tracking needed; we don't mutate op status here).
        var op = await opRepo.GetAsync(request.OperationId, cancellationToken);

        // Guard: wrong tenant (cross-tenant list snapshot mismatch).
        if (op is null || op.TenantId != request.TenantId)
            return Unit.Value;

        // Terminal operations need no SLA evaluation.
        if (OperationStateMachine.IsTerminal(op.Status))
            return Unit.Value;

        // User-wait states: customer must act — not an SLA breach.
        if (UserWaitStatuses.Contains(op.Status))
            return Unit.Value;

        var now = DateTimeOffset.UtcNow;
        var elapsed = now - op.UpdatedAt;

        // Derive thresholds from current status (§8).
        //
        // §8 SLA THRESHOLD SCOPING NOTE (spec §8 / §13 open decision):
        // (a) The §8 numbers (first-party: 2 min, external-provider: 10 min) are documented
        //     as placeholders — they are NOT yet normative per §13 ("first booking provider"
        //     is an open decision; no real provider integration exists at spec-06 time).
        // (b) The first-party-vs-external split is keyed on the booking PROVIDER (a §13 open
        //     decision), NOT on OperationTypeEnum (Create/Modify/Cancel/Reschedule). Branching
        //     on OperationType for SLA tiers would be incorrect; leave this switch status-based
        //     until the first real provider lands and §13 is resolved.
        // (c) Current approximations come from the status axis:
        //       Reconciling:           pending=0 s  / escalation=15 min (ReconcileEscalationSeconds)
        //       CompensationRequired:  (no pending)  escalation=30 min (CompensationEscalationSeconds)
        //       All other in-progress: pending=30 s  / escalation=10 min (InProgressEscalationSeconds)
        //     These are the best available proxies until a real provider SLA contract is known.
        int? pendingThreshold;
        int escalationThreshold;

        switch (op.Status)
        {
            case OperationStatus.Reconciling:
                pendingThreshold = ReconcilePendingSeconds;
                escalationThreshold = ReconcileEscalationSeconds;
                break;
            case OperationStatus.CompensationRequired:
                pendingThreshold = null; // no pending notification for this state
                escalationThreshold = CompensationEscalationSeconds;
                break;
            default:
                // Validating, ReservingCapacity, Held, Finalizing, Accepted, ManualReview
                pendingThreshold = InProgressPendingSeconds;
                escalationThreshold = InProgressEscalationSeconds;
                break;
        }

        var shouldEscalate = elapsed.TotalSeconds >= escalationThreshold;
        var shouldPend = !shouldEscalate && pendingThreshold.HasValue && elapsed.TotalSeconds >= pendingThreshold.Value;

        if (!shouldEscalate && !shouldPend)
            return Unit.Value;

        // All writes are in one transaction.
        // C8: re-read the op inside the transaction to guard against a concurrent
        // transition (e.g. op moved to terminal or user-wait) between the outer read and here.
        await uow.ExecuteInTransactionAsync(async ct =>
        {
            var opInTxn = await opRepo.GetAsync(request.OperationId, ct);
            if (opInTxn is null || opInTxn.TenantId != request.TenantId)
                return;
            if (OperationStateMachine.IsTerminal(opInTxn.Status))
                return;
            if (UserWaitStatuses.Contains(opInTxn.Status))
                return;

            // Re-derive thresholds for the in-txn status in case it changed between reads.
            int? innerPendingThreshold;
            int innerEscalationThreshold;
            switch (opInTxn.Status)
            {
                case OperationStatus.Reconciling:
                    innerPendingThreshold = ReconcilePendingSeconds;
                    innerEscalationThreshold = ReconcileEscalationSeconds;
                    break;
                case OperationStatus.CompensationRequired:
                    innerPendingThreshold = null;
                    innerEscalationThreshold = CompensationEscalationSeconds;
                    break;
                default:
                    innerPendingThreshold = InProgressPendingSeconds;
                    innerEscalationThreshold = InProgressEscalationSeconds;
                    break;
            }

            var innerElapsed = now - opInTxn.UpdatedAt;
            var innerShouldEscalate = innerElapsed.TotalSeconds >= innerEscalationThreshold;
            var innerShouldPend = !innerShouldEscalate
                && innerPendingThreshold.HasValue
                && innerElapsed.TotalSeconds >= innerPendingThreshold.Value;

            if (!innerShouldEscalate && !innerShouldPend)
                return;

            if (innerShouldEscalate)
            {
                await HandleEscalationAsync(request.TenantId, request.OperationId, now, ct);
            }
            else
            {
                await HandlePendingAsync(request.TenantId, request.OperationId, now, ct);
            }
        }, cancellationToken);

        return Unit.Value;
    }

    private async Task HandleEscalationAsync(
        string tenantId, string operationId, DateTimeOffset now, CancellationToken ct)
    {
        // Ensure exactly ONE open work item exists (idempotency).
        //
        // Atomic INSERT … ON CONFLICT DO NOTHING rather than read-then-add: a lost race on
        // read-then-add raises 23505, which aborts the entire ambient transaction rather than
        // producing the benign "someone else already opened it" this code intends. The return value
        // is the same signal the read gave — true when THIS call created the row — so the audit
        // below still fires exactly once per escalation.
        var created = await workItemRepo.TryAddOpenAsync(new OperationWorkItem
        {
            TenantId = tenantId,
            WorkItemId = $"wi_{Guid.NewGuid():N}",
            OperationId = operationId,
            Reason = "sla_escalation",
            State = "open",
            SlaDeadline = now.AddSeconds(DefaultOwnerTargetSeconds),
            CreatedAt = now,
            Version = 0,
        }, ct);

        if (created)
        {
            // Audit: only written when we actually create the work item.
            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = tenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "operation.sla_escalation",
                Actor = "system",
                SubjectRef = operationId,
                CorrelationId = operationId,
                OperationId = operationId,
                Metadata = JsonSerializer.Serialize(new { reason = "sla_escalation" }),
                OccurredAt = now,
            }, ct);
        }

        // Emit escalation notification ONCE per operation (deterministic message_id).
        var escalationMessageId = $"obx_sla_esc_{operationId}";
        var escalationExists = await outboxRepo.ExistsAsync(tenantId, escalationMessageId, ct);
        if (!escalationExists)
        {
            await outboxRepo.AddAsync(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = escalationMessageId,
                OperationId = operationId,
                MessageType = "notification.sla_escalation.v1",
                CorrelationId = operationId,
                Payload = JsonSerializer.Serialize(new
                {
                    operation_id = operationId,
                    tenant_id = tenantId,
                }),
                OccurredAt = now,
            }, ct);
        }
    }

    private async Task HandlePendingAsync(
        string tenantId, string operationId, DateTimeOffset now, CancellationToken ct)
    {
        // Emit still-confirming notification ONCE per operation (deterministic message_id).
        var pendingMessageId = $"obx_sla_pending_{operationId}";
        var pendingExists = await outboxRepo.ExistsAsync(tenantId, pendingMessageId, ct);
        if (!pendingExists)
        {
            await outboxRepo.AddAsync(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = pendingMessageId,
                OperationId = operationId,
                MessageType = "notification.still_confirming.v1",
                CorrelationId = operationId,
                Payload = JsonSerializer.Serialize(new
                {
                    operation_id = operationId,
                    tenant_id = tenantId,
                }),
                OccurredAt = now,
            }, ct);
        }
    }
}
