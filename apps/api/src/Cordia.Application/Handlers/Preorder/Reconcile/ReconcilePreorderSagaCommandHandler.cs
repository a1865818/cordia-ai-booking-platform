using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Preorder;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Preorder.Reconcile;

/// <summary>
/// Re-checks a stuck saga and either resumes it or escalates (spec 11 §5.3, Inv-7/Inv-9). Each
/// resumable saga-owned step maps to its own message type, so resuming is just re-enqueuing that step
/// — its own state-guard + checkpoints make the re-entry idempotent (a step that actually completed
/// has already advanced, so the re-enqueued message settles as a no-op). A saga waiting on the spec-06
/// engine (<c>CommittingBooking</c>, divergence #2) is nudged by re-emitting
/// <c>booking.command.requested.v1</c> — the 06 driver is idempotent and re-emits its terminal
/// booking-outcome signal if the operation already finished. A saga that never acquired anything
/// (Revalidating) fails clean; one already parked in Reconciling escalates to ManualReview. A saga
/// stuck in <c>CommittingPreorder</c> past <see cref="CommitStuckEscalationAfter"/> is not re-driven
/// forever: it escalates <c>CommittingPreorder → CompensatingBooking → ManualReview</c> (§21.4 /
/// FM#10 — booking committed, pre-order impossible → operator+customer resolve). Runs in
/// the inbox transaction; settles for unknown/terminal sagas.
/// </summary>
public sealed class ReconcilePreorderSagaCommandHandler(
    IPreorderSagaRepository sagaRepo,
    IBookingOperationRepository opRepo,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    ILogger<ReconcilePreorderSagaCommandHandler> logger)
    : IRequestHandler<ReconcilePreorderSagaCommand, Unit>
{
    /// <summary>
    /// How long a saga may sit in <see cref="PreorderSagaState.CommittingPreorder"/> (booking already
    /// committed, pre-order bind not landing) before reconcile stops re-driving the step and parks it
    /// for operator+customer resolution (§21.4 / §8 FM#10): CommittingPreorder → CompensatingBooking,
    /// whose handler advances to ManualReview and emits the neutral in-review notice (Inv-8).
    /// UpdatedAt is stamped only on state transitions, so age-in-state is a durable measure across
    /// step timeouts and recovery sweeps. Initial value is a §12 SLO placeholder.
    /// </summary>
    public static readonly TimeSpan CommitStuckEscalationAfter = TimeSpan.FromMinutes(30);

    /// <summary>06 operation states that are a terminal FAILURE for a delegated pre-order booking.</summary>
    private static readonly HashSet<OperationStatus> _terminalFailureStates =
    [
        OperationStatus.FailedFinal,
        OperationStatus.Rejected,
        OperationStatus.Expired,
        OperationStatus.AlternativesRequired, // no-capacity for a pre-order op (High #3) — not customer-recoverable
    ];

    /// <summary>
    /// Maps a resumable saga-owned state to the step message type that re-drives it. The dead
    /// hold-capacity / authorize-payment / commit-booking / compensate-hold steps were removed
    /// (booking is delegated to 06), so those states are no longer resumable here.
    /// <see cref="PreorderSagaState.CommittingBooking"/> is handled separately (06 re-nudge).
    /// </summary>
    private static string? ResumeMessageType(PreorderSagaState state) => state switch
    {
        PreorderSagaState.ReservingItems => PreorderSagaMessages.ReserveItems,
        PreorderSagaState.CommittingPreorder => PreorderSagaMessages.CommitPreorder,
        PreorderSagaState.CompensatingItems => PreorderSagaMessages.CompensateItems,
        PreorderSagaState.CompensatingBooking => PreorderSagaMessages.CompensateBooking,
        _ => null,
    };

    public async Task<Unit> Handle(ReconcilePreorderSagaCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var saga = await sagaRepo.GetAsync(command.SagaId, ct);
        if (saga is null || saga.TenantId != command.TenantId)
        {
            logger.LogInformation("Preorder reconcile: saga {SagaId} not found; settling.", command.SagaId);
            return Unit.Value;
        }

        if (saga.SagaState is PreorderSagaState.Completed
            or PreorderSagaState.FailedFinal
            or PreorderSagaState.ManualReview)
        {
            return Unit.Value; // already terminal — nothing to recover
        }

        // Waiting on the spec-06 engine (divergence #2). The saga advances only on the 06→saga
        // booking-outcome signal; if that single signal was lost (DLQ/poison) the saga would hang here
        // forever because re-nudging 06 on an ALREADY-terminal op re-emits nothing (the emit lives
        // inside 06's rows>0 transition guard). So probe the operation's real state and re-synthesize
        // the lost signal directly — the idempotent, state-guarded signal handlers do the advance.
        // Only when the op is still genuinely in-flight do we re-nudge 06 to make progress (Inv-7).
        if (saga.SagaState == PreorderSagaState.CommittingBooking)
        {
            var op = await opRepo.GetAsync(command.OperationId, ct);

            if (op is not null && op.TenantId == command.TenantId && op.Status == OperationStatus.Booked)
            {
                // Booking committed but the signal was lost → re-emit booking_committed so the saga
                // advances to CommittingPreorder and binds the pre-order.
                await outboxRepo.AddAsync(new OutboxMessage
                {
                    TenantId = command.TenantId,
                    MessageId = $"obx_{Guid.NewGuid():N}",
                    OperationId = command.OperationId,
                    MessageType = PreorderSagaMessages.BookingCommitted,
                    CorrelationId = command.CorrelationId ?? command.OperationId,
                    Payload = JsonSerializer.Serialize(new
                    {
                        operation_id = command.OperationId,
                        tenant_id = command.TenantId,
                        booking_id = op.BookingId,
                    }),
                    OccurredAt = now,
                }, ct);
                await auditRepo.AddAsync(PreorderSagaSupport.Audit(
                    command.TenantId, "preorder.reconcile_resumed", command.SagaId, command.OperationId, now,
                    new { saga_id = command.SagaId, resumed_state = saga.SagaState.ToString(), op_status = op.Status.ToString(), resignalled = "booking_committed" }), ct);
                return Unit.Value;
            }

            if (op is not null && op.TenantId == command.TenantId && _terminalFailureStates.Contains(op.Status))
            {
                // Booking terminally failed but the signal was lost → re-emit booking_failed so the saga
                // compensates the reserved items and surfaces the one combined outcome.
                await outboxRepo.AddAsync(new OutboxMessage
                {
                    TenantId = command.TenantId,
                    MessageId = $"obx_{Guid.NewGuid():N}",
                    OperationId = command.OperationId,
                    MessageType = PreorderSagaMessages.BookingFailed,
                    CorrelationId = command.CorrelationId ?? command.OperationId,
                    Payload = JsonSerializer.Serialize(new
                    {
                        operation_id = command.OperationId,
                        tenant_id = command.TenantId,
                        reason = $"reconcile:{op.Status}",
                    }),
                    OccurredAt = now,
                }, ct);
                await auditRepo.AddAsync(PreorderSagaSupport.Audit(
                    command.TenantId, "preorder.reconcile_resumed", command.SagaId, command.OperationId, now,
                    new { saga_id = command.SagaId, resumed_state = saga.SagaState.ToString(), op_status = op.Status.ToString(), resignalled = "booking_failed" }), ct);
                return Unit.Value;
            }

            // Op parked in 06's own ManualReview — a human owns it, and the 06 driver no-ops for
            // that state, so a re-nudge would spin forever without progress (review 2026-07-03,
            // C16). Settle: ResolveManualReviewCommandHandler routes its terminal resolution
            // through the IsPreorder-gated outcome types, so the saga WILL receive the
            // booking_committed / booking_failed signal when the operator resolves it.
            if (op is not null && op.TenantId == command.TenantId && op.Status == OperationStatus.ManualReview)
            {
                logger.LogInformation(
                    "Preorder reconcile: operation {OperationId} is in ManualReview (human-owned); " +
                    "settling — the 06 resolution will signal saga {SagaId}.",
                    command.OperationId, command.SagaId);
                return Unit.Value;
            }

            // Op still in-flight (or unknown) — re-nudge the idempotent 06 driver to make progress.
            await outboxRepo.AddAsync(new OutboxMessage
            {
                TenantId = command.TenantId,
                MessageId = $"obx_{Guid.NewGuid():N}",
                OperationId = command.OperationId,
                MessageType = "booking.command.requested.v1",
                CorrelationId = command.CorrelationId ?? command.OperationId,
                Payload = JsonSerializer.Serialize(new
                {
                    operation_id = command.OperationId,
                    tenant_id = command.TenantId,
                }),
                OccurredAt = now,
            }, ct);
            await auditRepo.AddAsync(PreorderSagaSupport.Audit(
                command.TenantId, "preorder.reconcile_resumed", command.SagaId, command.OperationId, now,
                new { saga_id = command.SagaId, resumed_state = saga.SagaState.ToString(), op_status = op?.Status.ToString(), renudged_06 = true }), ct);
            return Unit.Value;
        }

        // §21.4 / FM#10: the booking is committed but the pre-order bind has not landed for the whole
        // escalation window — the step timeout and recovery sweeps have been re-driving CommitPreorder
        // without progress. Stop re-driving (it would loop forever) and park for a human:
        // CommittingPreorder → CompensatingBooking → ManualReview (operator+customer convert or cancel).
        if (saga.SagaState == PreorderSagaState.CommittingPreorder
            && now - saga.UpdatedAt >= CommitStuckEscalationAfter)
        {
            PreorderSagaStateMachine.EnsureAllowed(
                PreorderSagaState.CommittingPreorder, PreorderSagaState.CompensatingBooking);
            var escalated = await sagaRepo.TryAdvanceStateAsync(
                command.TenantId, command.SagaId, saga.Version,
                PreorderSagaState.CommittingPreorder, PreorderSagaState.CompensatingBooking, now,
                setFailureReason: "commit_preorder_stuck", ct: ct);
            if (escalated > 0)
            {
                await outboxRepo.AddAsync(PreorderSagaMessages.StepMessage(
                    command.TenantId, PreorderSagaMessages.CompensateBooking,
                    command.SagaId, command.OperationId, now, command.CorrelationId), ct);
                await auditRepo.AddAsync(PreorderSagaSupport.Audit(
                    command.TenantId, "preorder.reconcile_escalated", command.SagaId, command.OperationId, now,
                    new
                    {
                        saga_id = command.SagaId,
                        to = PreorderSagaState.CompensatingBooking.ToString(),
                        stuck_since = saga.UpdatedAt,
                    }), ct);
            }
            return Unit.Value;
        }

        var resume = ResumeMessageType(saga.SagaState);
        if (resume is not null)
        {
            await outboxRepo.AddAsync(
                PreorderSagaMessages.StepMessage(command.TenantId, resume, command.SagaId, command.OperationId, now, command.CorrelationId), ct);
            await auditRepo.AddAsync(PreorderSagaSupport.Audit(
                command.TenantId, "preorder.reconcile_resumed", command.SagaId, command.OperationId, now,
                new { saga_id = command.SagaId, resumed_state = saga.SagaState.ToString() }), ct);
            return Unit.Value;
        }

        // Non-resumable: Revalidating acquired nothing → fail clean (terminal); Reconciling is already
        // parked → escalate to ManualReview (awaits a human). ANY other (legacy/unknown) state must NOT
        // throw — a reconcile message is queue-driven and an unhandled throw would poison/retry it
        // forever. Settle instead.
        PreorderSagaState next;
        if (saga.SagaState == PreorderSagaState.Revalidating)
            next = PreorderSagaState.FailedFinal;
        else if (saga.SagaState == PreorderSagaState.Reconciling)
            next = PreorderSagaState.ManualReview;
        else
        {
            logger.LogWarning(
                "Preorder reconcile: saga {SagaId} in non-recoverable state {State}; settling without escalation.",
                command.SagaId, saga.SagaState);
            return Unit.Value;
        }

        PreorderSagaStateMachine.EnsureAllowed(saga.SagaState, next);
        var won = await sagaRepo.TryAdvanceStateAsync(
            command.TenantId, command.SagaId, saga.Version, saga.SagaState, next, now,
            setFailureReason: $"reconcile_escalated:{saga.SagaState}", ct: ct);
        if (won == 0)
            return Unit.Value;

        // Inv-8: ManualReview is NOT a terminal customer outcome — emit only a neutral "under review"
        // notice so the terminal convert/cancel still owns the single combined outcome. Only the
        // terminal FailedFinal escalation emits the failure outcome.
        await outboxRepo.AddAsync(
            next == PreorderSagaState.FailedFinal
                ? PreorderSagaSupport.FailureOutcome(
                    command.TenantId, command.SagaId, command.OperationId, $"reconcile_{next}", now, command.CorrelationId)
                : PreorderSagaSupport.InReviewNotice(
                    command.TenantId, command.SagaId, command.OperationId, now, command.CorrelationId), ct);
        await auditRepo.AddAsync(PreorderSagaSupport.Audit(
            command.TenantId, "preorder.reconcile_escalated", command.SagaId, command.OperationId, now,
            new { saga_id = command.SagaId, to = next.ToString() }), ct);
        return Unit.Value;
    }
}
