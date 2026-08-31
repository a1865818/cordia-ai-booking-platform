using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Observability;
using Cordia.Infrastructure.Observability;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.Compensate;

/// <summary>
/// Resolves an operation in <c>CompensationRequired</c> status (spec 06 §9 FM#12, §11).
///
/// Protocol:
///   1. Load op; guard status == CompensationRequired.
///   2. Determine whether a booking effect exists to undo: look up the latest "finalize" attempt
///      for a ProviderRef.
///   3. Create-or-skip the "compensate" workflow step (InProgress).
///   4a. ProviderRef present → call adapter.CancelBookingAsync (OUTSIDE transaction).
///       - Created / AlreadyExists / NotFound (cancel acknowledged / already gone):
///           TXN: step→Succeeded; op→FailedFinal (reason "compensated"); outbox
///           notification.booking_failed.v1; audit operation.compensated. Return Terminal.
///       - Ambiguous / Transient / PermanentError / AuthError (compensation unclear):
///           TXN: manualReviewEscalator (CompensationRequired→ManualReview). Return Wait.
///   4a-bis. ProviderRef null but the latest finalize attempt is Dispatching/Ambiguous
///       (the #51 cancel-before-create race): the effect is UNKNOWN, not absent.
///           TXN: manualReviewEscalator (CompensationRequired→ManualReview,
///           reason "compensation_effect_unresolved"). Return Wait.
///   4b. ProviderRef null AND no attempt in flight (nothing was created):
///           TXN: step→Succeeded; op→FailedFinal (reason "failed_no_effect"); outbox
///           notification.booking_failed.v1; audit operation.failed_final. Return Terminal.
///
/// All op transitions are version-guarded (rows==0 → continue/wait, skip dependent writes).
/// The CancelBookingAsync external call is made OUTSIDE the transaction.
/// </summary>
public sealed class CompensateOperationCommandHandler(
    IBookingOperationRepository opRepo,
    IWorkflowStepRepository stepRepo,
    IWorkflowAttemptRepository attemptRepo,
    IBookingProviderAdapter adapter,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    ManualReviewEscalator manualReviewEscalator,
    IUnitOfWork uow,
    ICapacityReservation capacityReservation,
    IBookingRepository bookingRepo,
    Metrics metrics)
    : IRequestHandler<CompensateOperationCommand, StepOutcome>
{
    private const string CompensateStepType = "compensate";
    private const string ProviderAccount = "default";

    public async Task<StepOutcome> Handle(CompensateOperationCommand command, CancellationToken ct)
    {
        var op = await opRepo.GetAsync(command.OperationId, ct);

        if (op is null || op.TenantId != command.TenantId)
            return StepOutcome.Wait;

        if (op.Status != OperationStatus.CompensationRequired)
            return StepOutcome.Continue;

        var tenantId = command.TenantId;
        var operationId = command.OperationId;

        // Determine whether a booking was created that needs to be undone.
        //
        // BOTH fields matter. Before #51 this handler read only ProviderRef and never looked at
        // Outcome at all, which made an in-flight attempt look exactly like no attempt.
        var latest = await attemptRepo.GetLatestForStepAsync(tenantId, operationId, "finalize", ct);
        var providerRef = latest?.ProviderRef;

        // ─────────────────────────────────────────────────────────────
        // Create-or-skip the "compensate" workflow step (idempotent).
        // ─────────────────────────────────────────────────────────────
        WorkflowStep? compensateStep = null;
        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            var now = DateTimeOffset.UtcNow;
            compensateStep = await stepRepo.GetOrAddAsync(new WorkflowStep
            {
                TenantId = tenantId,
                WorkflowStepId = $"ws_{Guid.NewGuid():N}",
                OperationId = operationId,
                StepType = CompensateStepType,
                Status = WorkflowStepStatus.InProgress,
                ProviderIdempotencyKey = string.Empty,
                AttemptCount = 1,
                Version = 0,
                CreatedAt = now,
                UpdatedAt = now,
            }, ct2);
        }, ct);

        if (compensateStep is null)
            return StepOutcome.Continue;

        // ─────────────────────────────────────────────────────────────
        // 4a. ProviderRef present → cancel the booking with the provider.
        //     External call OUTSIDE the transaction.
        // ─────────────────────────────────────────────────────────────
        if (providerRef is not null)
        {
            var cancelResult = await adapter.CancelBookingAsync(ProviderAccount, providerRef, ct);

            // Cancel acknowledged (booking gone, or already absent): → FailedFinal.
            if (cancelResult.Kind is ProviderResultKind.Created
                or ProviderResultKind.AlreadyExists
                or ProviderResultKind.NotFound)
            {
                var now = DateTimeOffset.UtcNow;
                var committed = false;
                await uow.ExecuteInTransactionAsync(async ct2 =>
                {
                    // Mark compensate step Succeeded.
                    if (compensateStep.Status == WorkflowStepStatus.InProgress)
                    {
                        await stepRepo.TryTransitionStatusAsync(
                            tenantId, compensateStep.WorkflowStepId, compensateStep.Version,
                            WorkflowStepStatus.InProgress, WorkflowStepStatus.Succeeded, ct2);
                    }

                    // Version-guarded transition CompensationRequired → FailedFinal.
                    var freshOp = await opRepo.GetAsync(operationId, ct2);
                    if (freshOp is null || freshOp.Status != OperationStatus.CompensationRequired)
                        return;

                    var rows = await opRepo.TryTransitionStatusAsync(
                        tenantId, operationId, freshOp.Version,
                        OperationStatus.CompensationRequired, OperationStatus.FailedFinal,
                        reason: "compensated", ct: ct2);

                    if (rows > 0)
                    {
                        committed = true;

                        // §6.1 T7: free first-party capacity atomically with the FailedFinal
                        // transition. Idempotent — no-op if no active hold exists (non-tracked op
                        // or hold already released/finalized at finalize time).
                        await capacityReservation.ReleaseByOperationAsync(
                            tenantId, operationId, ReleaseReason.Compensated, ct2);

                        // A compensated MUTATION gives the booking row its lifecycle back too. The
                        // seat is released here and the booking left parked at
                        // Modification/CancellationPending — the operation and the row it acted on
                        // disagreeing about the same event, with FailedFinal terminal so nothing
                        // ever revisits it.
                        await BookingPendingLifecycle.RevertOnFailureAsync(
                            bookingRepo, tenantId, freshOp, DateTimeOffset.UtcNow, ct2);

                        // Spec 11 §5.3: a pre-order op signals the saga (→ compensate items) instead
                        // of emitting the customer failure notification directly.
                        await outboxRepo.AddAsync(new OutboxMessage
                        {
                            TenantId = tenantId,
                            MessageId = $"obx_{Guid.NewGuid():N}",
                            OperationId = operationId,
                            MessageType = Cordia.Application.Handlers.Preorder.PreorderSagaMessages.FailedOutcomeType(freshOp.IsPreorder),
                            CorrelationId = operationId,
                            Payload = JsonSerializer.Serialize(new
                            {
                                operation_id = operationId,
                                tenant_id = tenantId,
                                reason = "compensated",
                            }),
                            OccurredAt = now,
                        }, ct2);

                        await auditRepo.AddAsync(new AuditEvent
                        {
                            TenantId = tenantId,
                            EventId = $"evt_{Guid.NewGuid():N}",
                            EventType = "operation.compensated",
                            Actor = "system",
                            SubjectRef = operationId,
                            CorrelationId = operationId,
                            OperationId = operationId,
                            Metadata = JsonSerializer.Serialize(new
                            {
                                provider_ref = providerRef,
                                reason = "compensated",
                            }),
                            OccurredAt = now,
                        }, ct2);
                        metrics.FulfillmentCompensationSuccessTotal.Add(1);
                        metrics.FulfillmentOperationTerminalTotal.Add(1, new KeyValuePair<string, object?>(MetricNames.Tags.State, "failed"));
                    }
                }, ct);

                return committed ? StepOutcome.Terminal : StepOutcome.Continue;
            }

            // Cancel ambiguous / transient / permanent / auth error → ManualReview.
            // (§11: CompensationRequired → ManualReview)
            // On a lost version race (escalated == false) return Continue so the driver reloads
            // and re-evaluates; on success return Wait (a human resolves the work item).
            var escalateOutcome = StepOutcome.Continue;
            await uow.ExecuteInTransactionAsync(async ct2 =>
            {
                var freshOp = await opRepo.GetAsync(operationId, ct2);
                if (freshOp is null || freshOp.Status != OperationStatus.CompensationRequired)
                    return;

                var escalated = await manualReviewEscalator.EscalateAsync(
                    tenantId,
                    operationId,
                    freshOp.Version,
                    OperationStatus.CompensationRequired,
                    reason: "compensation_ambiguous",
                    slaDeadline: DateTimeOffset.UtcNow.AddHours(2),
                    ct: ct2);

                if (escalated)
                {
                    metrics.FulfillmentCompensationFailureTotal.Add(1);
                    escalateOutcome = StepOutcome.Wait;
                }
            }, ct);

            return escalateOutcome;
        }

        // ─────────────────────────────────────────────────────────────
        // 4a-bis. ProviderRef null BUT the latest finalize attempt is still in flight (#51).
        //
        // A null ProviderRef is not the same fact as "no booking was created". It means only
        // that no reference has been WRITTEN YET. FinalizeBookingCommandHandler assigns
        // attempt.ProviderRef in TXN2 (:432), long after it flips the attempt to Dispatching
        // (:404-405) and calls the provider (:412) — so for the whole duration of the provider
        // call the row reads {Outcome: Dispatching, ProviderRef: null}, which is indistinguishable
        // from "nothing was ever attempted" to a reader that looks only at the reference.
        //
        // FM#13 is built on that distinction and gets it right: it routes to CompensationRequired
        // only when the attempt is null or Planned, and sends Dispatching/Ambiguous to the Inv-7
        // reconcile path instead (Finalize:196-210, :218-223). Reaching this handler with an
        // in-flight attempt therefore means the cancel-before-create race actually happened —
        // FM#13's re-read at Finalize:239-240 is a non-locking AsNoTracking SELECT under READ
        // COMMITTED, so a concurrent dispatch can flip Planned → Dispatching after that read and
        // before the CAS commits.
        //
        // Declaring "failed_no_effect" here would be a false authoritative outcome (Inv-16): it
        // terminalises the operation, releases the first-party hold, reverts the booking
        // lifecycle, and tells the guest their booking failed — while a real reservation exists
        // at the venue that nothing will ever reference again. Escalate instead; an operator can
        // resolve to Reconciling and let the §16.2 ladder establish what actually happened.
        //
        // Planned is deliberately NOT in this set. A Planned attempt has provably not been
        // dispatched, so FM#13's fast path stays fast; Phase 5 (#51b) closes the
        // Planned → Dispatching window at source by fencing the dispatch write itself.
        // ─────────────────────────────────────────────────────────────
        if (latest is { Outcome: AttemptOutcome.Dispatching or AttemptOutcome.Ambiguous })
        {
            var unresolvedOutcome = StepOutcome.Continue;
            await uow.ExecuteInTransactionAsync(async ct2 =>
            {
                var freshOp = await opRepo.GetAsync(operationId, ct2);
                if (freshOp is null || freshOp.Status != OperationStatus.CompensationRequired)
                    return;

                var escalated = await manualReviewEscalator.EscalateAsync(
                    tenantId,
                    operationId,
                    freshOp.Version,
                    OperationStatus.CompensationRequired,
                    reason: "compensation_effect_unresolved",
                    slaDeadline: DateTimeOffset.UtcNow.AddHours(2),
                    ct: ct2);

                if (escalated)
                {
                    metrics.FulfillmentCompensationFailureTotal.Add(1);
                    unresolvedOutcome = StepOutcome.Wait;
                }
            }, ct);

            return unresolvedOutcome;
        }

        // ─────────────────────────────────────────────────────────────
        // 4b. ProviderRef null AND no attempt is in flight → nothing was created; FailedFinal.
        // ─────────────────────────────────────────────────────────────
        var nullRefCommitted = false;
        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            var now = DateTimeOffset.UtcNow;

            // Mark compensate step Succeeded.
            if (compensateStep.Status == WorkflowStepStatus.InProgress)
            {
                await stepRepo.TryTransitionStatusAsync(
                    tenantId, compensateStep.WorkflowStepId, compensateStep.Version,
                    WorkflowStepStatus.InProgress, WorkflowStepStatus.Succeeded, ct2);
            }

            // Version-guarded transition CompensationRequired → FailedFinal.
            var freshOp = await opRepo.GetAsync(operationId, ct2);
            if (freshOp is null || freshOp.Status != OperationStatus.CompensationRequired)
                return;

            var rows = await opRepo.TryTransitionStatusAsync(
                tenantId, operationId, freshOp.Version,
                OperationStatus.CompensationRequired, OperationStatus.FailedFinal,
                reason: "failed_no_effect", ct: ct2);

            if (rows > 0)
            {
                nullRefCommitted = true;

                // §6.1 T7: free first-party capacity atomically with the FailedFinal
                // transition. Idempotent — no-op if no active hold exists (non-tracked op
                // or hold already released/finalized at finalize time).
                await capacityReservation.ReleaseByOperationAsync(
                    tenantId, operationId, ReleaseReason.Compensated, ct2);

                // Same on the no-effect route: nothing happened to the booking, so the pending
                // status the manage-booking door parked must not outlive the operation.
                await BookingPendingLifecycle.RevertOnFailureAsync(
                    bookingRepo, tenantId, freshOp, DateTimeOffset.UtcNow, ct2);

                // Spec 11 §5.3: a pre-order op signals the saga instead of notifying the customer.
                await outboxRepo.AddAsync(new OutboxMessage
                {
                    TenantId = tenantId,
                    MessageId = $"obx_{Guid.NewGuid():N}",
                    OperationId = operationId,
                    MessageType = Cordia.Application.Handlers.Preorder.PreorderSagaMessages.FailedOutcomeType(freshOp.IsPreorder),
                    CorrelationId = operationId,
                    Payload = JsonSerializer.Serialize(new
                    {
                        operation_id = operationId,
                        tenant_id = tenantId,
                        reason = "failed_no_effect",
                    }),
                    OccurredAt = now,
                }, ct2);

                await auditRepo.AddAsync(new AuditEvent
                {
                    TenantId = tenantId,
                    EventId = $"evt_{Guid.NewGuid():N}",
                    EventType = "operation.failed_final",
                    Actor = "system",
                    SubjectRef = operationId,
                    CorrelationId = operationId,
                    OperationId = operationId,
                    Metadata = JsonSerializer.Serialize(new { reason = "failed_no_effect" }),
                    OccurredAt = now,
                }, ct2);
                metrics.FulfillmentCompensationSuccessTotal.Add(1);
                metrics.FulfillmentOperationTerminalTotal.Add(1, new KeyValuePair<string, object?>(MetricNames.Tags.State, "failed"));
            }
        }, ct);

        return nullRefCommitted ? StepOutcome.Terminal : StepOutcome.Continue;
    }
}
