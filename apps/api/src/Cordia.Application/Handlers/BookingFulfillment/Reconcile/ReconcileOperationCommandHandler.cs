using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.Reconcile;

/// <summary>
/// Implements the §16.2 reconciliation lookup order for an operation in <c>Reconciling</c> status
/// (spec 06 §16, Inv-7, FM#2 crash-after-provider).
///
/// Lookup order (load-bearing — Inv-7):
///   1. LookupByIdempotencyKey  (fastest; proves idempotent create matched)
///   2. LookupByReference       (if key lookup returns NotFound AND a ProviderRef was recorded)
///   3. GetStatus               (if reference lookup also returns NotFound AND ProviderRef present)
///
/// Effect proven (Created / AlreadyExists + reference):
///   In ONE transaction — update/create the Reconciled attempt row, mark finalize step Succeeded,
///   transition op Reconciling → Booked, add notification outbox row (Inv-8), add audit event.
///   Returns <see cref="StepOutcome.Terminal"/>.
///
/// No effect proven (all lookups NotFound):
///   Budget not exhausted → Reconciling → Held (re-create under same key, Inv-7).
///     Returns <see cref="StepOutcome.Continue"/>.
///   Budget exhausted (FM#11) → escalate to ManualReview via <see cref="ManualReviewEscalator"/>
///     (work item + notification.still_confirming.v1 + audit).
///     Returns <see cref="StepOutcome.Wait"/>.
/// </summary>
public sealed class ReconcileOperationCommandHandler(
    IBookingOperationRepository opRepo,
    IWorkflowStepRepository stepRepo,
    IWorkflowAttemptRepository attemptRepo,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    IBookingProviderAdapter adapter,
    IRetryPolicy retryPolicy,
    ManualReviewEscalator manualReviewEscalator,
    IUnitOfWork uow)
    : IRequestHandler<ReconcileOperationCommand, StepOutcome>
{
    private const string StepType = "finalize";
    private const string ProviderAccount = "default";

    public async Task<StepOutcome> Handle(ReconcileOperationCommand command, CancellationToken ct)
    {
        var op = await opRepo.GetAsync(command.OperationId, ct);

        if (op is null || op.TenantId != command.TenantId)
            return StepOutcome.Wait;

        if (op.Status != OperationStatus.Reconciling)
            return StepOutcome.Continue; // driver re-evaluates; op may have already advanced

        var tenantId = command.TenantId;
        var operationId = command.OperationId;

        // Derive the stable idempotency key for the finalize step (Inv-2/3/7).
        var step = await stepRepo.GetByOperationAndTypeAsync(tenantId, operationId, StepType, ct);
        var latest = await attemptRepo.GetLatestForStepAsync(tenantId, operationId, StepType, ct);

        var providerKey = step?.ProviderIdempotencyKey
            ?? latest?.ProviderIdempotencyKey
            ?? ProviderIdempotencyKey.Derive(operationId, StepType, op.IntentRevision);

        // ─────────────────────────────────────────────────────────────
        // §16.2 Lookup order (load-bearing — Inv-7):
        //   1. idempotency key  2. reference (if key NotFound + ref known)  3. status (if ref NotFound)
        // ─────────────────────────────────────────────────────────────
        var r = await adapter.LookupByIdempotencyKeyAsync(ProviderAccount, providerKey, ct);
        var via = "idempotency_key";

        if (r.Kind == ProviderResultKind.NotFound && latest?.ProviderRef is not null)
        {
            r = await adapter.LookupByReferenceAsync(ProviderAccount, latest.ProviderRef, ct);
            via = "reference";
        }

        if (r.Kind == ProviderResultKind.NotFound && latest?.ProviderRef is not null)
        {
            r = await adapter.GetStatusAsync(ProviderAccount, latest.ProviderRef, ct);
            via = "status";
        }

        var effectProven = r.Kind is ProviderResultKind.Created or ProviderResultKind.AlreadyExists
            && r.ProviderReference is not null;

        if (effectProven)
        {
            // FM#13: if a cancellation was requested while the op was Reconciling AND the effect
            // is now proven, a human must resolve whether to honour the booking or cancel it.
            // Escalate to ManualReview (reason "cancel_raced_reconciled_booking").
            if (op.CancellationRequestedAt.HasValue)
            {
                await uow.ExecuteInTransactionAsync(async ct2 =>
                {
                    var opInsideTxn = await opRepo.GetAsync(operationId, ct2);
                    if (opInsideTxn is not null && opInsideTxn.Status == OperationStatus.Reconciling)
                    {
                        await manualReviewEscalator.EscalateAsync(
                            tenantId,
                            operationId,
                            opInsideTxn.Version,
                            OperationStatus.Reconciling,
                            reason: "cancel_raced_reconciled_booking",
                            slaDeadline: DateTimeOffset.UtcNow.AddMinutes(30),
                            ct: ct2);
                    }
                }, ct);

                return StepOutcome.Wait;
            }

            // Effect found — record reconciliation result and advance to Booked.
            await uow.ExecuteInTransactionAsync(async ct2 =>
            {
                var now = DateTimeOffset.UtcNow;
                var reconciliationMeta = JsonSerializer.Serialize(new
                {
                    resolved_by = "lookup",
                    via,
                    provider_reference = r.ProviderReference,
                });

                // Update or create the reconciled attempt row.
                if (latest is not null)
                {
                    latest.Outcome = AttemptOutcome.Reconciled;
                    latest.ProviderRef = r.ProviderReference;
                    latest.CompletedAt = now;
                    latest.Reconciliation = reconciliationMeta;
                    await attemptRepo.UpdateAsync(latest, ct2);
                }
                else
                {
                    // No attempt row existed (crashed before TXN1 insertion); create one now.
                    await attemptRepo.AddAsync(new WorkflowAttempt
                    {
                        TenantId = tenantId,
                        AttemptId = $"att_{Guid.NewGuid():N}",
                        OperationId = operationId,
                        WorkflowStep = StepType,
                        ProviderIdempotencyKey = providerKey,
                        ProviderName = adapter.ProviderName,
                        ProviderAccount = ProviderAccount,
                        RequestHash = string.Empty,
                        AttemptNumber = 1,
                        StartedAt = now,
                        CompletedAt = now,
                        Outcome = AttemptOutcome.Reconciled,
                        ProviderRef = r.ProviderReference,
                        RequestMeta = "{}",
                        ResponseMeta = "{}",
                        Reconciliation = reconciliationMeta,
                    }, ct2);
                }

                // Transition finalize step → Succeeded (if non-terminal).
                if (step is not null && step.Status == WorkflowStepStatus.InProgress)
                {
                    await stepRepo.TryTransitionStatusAsync(
                        tenantId, step.WorkflowStepId, step.Version,
                        WorkflowStepStatus.InProgress, WorkflowStepStatus.Succeeded, ct2);
                }

                // Version-guarded op transition: Reconciling → Booked.
                var freshOp = await opRepo.GetAsync(operationId, ct2);
                if (freshOp is not null && freshOp.Status == OperationStatus.Reconciling)
                {
                    var rows = await opRepo.TryTransitionStatusAsync(
                        tenantId, operationId, freshOp.Version,
                        OperationStatus.Reconciling, OperationStatus.Booked,
                        reason: "reconciled_booked",
                        bookingId: r.ProviderReference,
                        ct: ct2);

                    if (rows > 0)
                    {
                        // Inv-8: outcome outbox row in the SAME transaction as the Booked transition.
                        // Spec 11 §5.3: a pre-order op signals the saga instead of notifying directly.
                        await outboxRepo.AddAsync(new OutboxMessage
                        {
                            TenantId = tenantId,
                            MessageId = $"obx_{Guid.NewGuid():N}",
                            OperationId = operationId,
                            MessageType = Cordia.Application.Handlers.Preorder.PreorderSagaMessages.ConfirmedOutcomeType(freshOp.IsPreorder),
                            CorrelationId = operationId,
                            Payload = JsonSerializer.Serialize(new
                            {
                                operation_id = operationId,
                                booking_id = r.ProviderReference,
                                tenant_id = tenantId,
                            }),
                            OccurredAt = now,
                        }, ct2);

                        await auditRepo.AddAsync(new AuditEvent
                        {
                            TenantId = tenantId,
                            EventId = $"evt_{Guid.NewGuid():N}",
                            EventType = "operation.reconciled",
                            Actor = "system",
                            SubjectRef = operationId,
                            CorrelationId = operationId,
                            OperationId = operationId,
                            Metadata = JsonSerializer.Serialize(new
                            {
                                booking_id = r.ProviderReference,
                                resolution_path = via,
                                provider = adapter.ProviderName,
                            }),
                            OccurredAt = now,
                        }, ct2);
                    }
                }
            }, ct);

            return StepOutcome.Terminal;
        }

        // No effect proven — check the finalize step's retry budget before re-creating.
        // Use the finalize step's CreatedAt as the first-attempt proxy and the latest attempt's
        // AttemptNumber as the completed count.
        var latestAttemptNumber = latest?.AttemptNumber ?? 1;
        var firstAttemptAt = step?.CreatedAt ?? DateTimeOffset.UtcNow;
        var now = DateTimeOffset.UtcNow;

        var nextEligible = retryPolicy.NextEligibleAt(
            StepRetryBudget.ForCreateBooking,
            latestAttemptNumber,
            firstAttemptAt,
            now);

        if (nextEligible == null)
        {
            // Budget exhausted (FM#11): escalate to ManualReview.
            // ManualReviewEscalator performs the transition + work item + outbox + audit
            // inside a single transaction it controls (shares the scoped DbContext/UoW).
            var freshOpForEscalate = await opRepo.GetAsync(operationId, ct);
            if (freshOpForEscalate is not null && freshOpForEscalate.Status == OperationStatus.Reconciling)
            {
                await uow.ExecuteInTransactionAsync(async ct2 =>
                {
                    // Re-read inside transaction to get a consistent version.
                    var opInsideTxn = await opRepo.GetAsync(operationId, ct2);
                    if (opInsideTxn is not null && opInsideTxn.Status == OperationStatus.Reconciling)
                    {
                        await manualReviewEscalator.EscalateAsync(
                            tenantId,
                            operationId,
                            opInsideTxn.Version,
                            OperationStatus.Reconciling,
                            reason: "reconcile_no_effect_budget_exhausted",
                            slaDeadline: now.AddHours(1),
                            ct: ct2);
                    }
                }, ct);
            }

            return StepOutcome.Wait;
        }

        // Budget not exhausted — re-create is safe under the same idempotency key (Inv-7).
        // Transition Reconciling → Held so the driver re-enters the payment/finalize path.
        // Mark the old Dispatching/Ambiguous attempt Abandoned so FinalizeBookingCommandHandler's
        // Inv-7 guard does NOT block the re-create when it next sees the op in Finalizing.
        // The step row retains its ProviderIdempotencyKey so Finalize reuses it.
        var currentOp2 = await opRepo.GetAsync(operationId, ct);
        if (currentOp2 is not null && currentOp2.Status == OperationStatus.Reconciling)
        {
            await uow.ExecuteInTransactionAsync(async ct2 =>
            {
                // Abandon the stale Dispatching/Ambiguous attempt inside the same txn as the
                // Reconciling→Held transition so there is no window where Finalizing sees it.
                var latestForAbandon = await attemptRepo.GetLatestForStepAsync(tenantId, operationId, StepType, ct2);
                if (latestForAbandon is { Outcome: AttemptOutcome.Dispatching or AttemptOutcome.Ambiguous })
                {
                    latestForAbandon.Outcome = AttemptOutcome.Abandoned;
                    latestForAbandon.CompletedAt = DateTimeOffset.UtcNow;
                    await attemptRepo.UpdateAsync(latestForAbandon, ct2);
                }

                var op2InTxn = await opRepo.GetAsync(operationId, ct2);
                if (op2InTxn is not null && op2InTxn.Status == OperationStatus.Reconciling)
                {
                    await opRepo.TryTransitionStatusAsync(
                        tenantId, operationId, op2InTxn.Version,
                        OperationStatus.Reconciling, OperationStatus.Held,
                        reason: "reconcile_no_effect_recreate",
                        ct: ct2);
                }
            }, ct);
        }

        return StepOutcome.Continue;
    }
}
