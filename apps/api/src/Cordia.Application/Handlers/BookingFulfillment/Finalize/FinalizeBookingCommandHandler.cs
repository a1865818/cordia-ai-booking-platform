using System.Text.Json;
using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Observability;
using Cordia.Infrastructure.Observability;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.Finalize;


/// <summary>
/// Canonical §6.2 staged-worker implementation for the "finalize" step.
///
/// Protocol (Inv-4, Inv-7, Inv-8):
///   TXN1: Held→Finalizing (if not already); create/load workflow_step; insert Planned attempt.
///   Circuit check: if circuit open → abandon the Planned attempt + schedule durable retry → Reschedule.
///   Mark Dispatching (best-effort, own write, no explicit txn).
///   External call: adapter.CreateBookingAsync.
///   TXN2 (success): attempt=Succeeded + step=Succeeded + op→Booked + outbox + audit.
///   TXN2 (transient): attempt=TransientFailure + circuit.RecordFailure.
///     Budget not exhausted → schedule durable retry → Reschedule (op stays Finalizing).
///     Budget exhausted (FM#11) → Finalizing→Reconciling (reason "retry_budget_exhausted")
///     so the driver dispatches ReconcileOperationCommand, which (no-effect + exhausted) escalates
///     to ManualReview.
///   TXN2 (ambiguous): Finalizing→Reconciling (§16.2 lookup before re-create) — unchanged from T5.
///   TXN2 (NoCapacity at finalize): anomalous (§11: Finalizing has NO edge to AlternativesRequired) →
///     Finalizing→Reconciling (reason "unexpected_no_capacity").
///   TXN2 (Rejected/PermanentError): persist attempt → Finalizing→CompensationRequired
///     (driver dispatches CompensateOperationCommand).
///   TXN2 (AuthError/PaymentActionRequired): persist attempt (Abandoned) → Finalizing→Reconciling
///     (reconcile no-effect+escalation → ManualReview; §11 forbids direct Finalizing→ManualReview).
///   FM#13 (cancel-before-create): if CancellationRequestedAt != null at the top of finalize and
///     there is no in-flight attempt, skip straight to CompensationRequired.
///   FM#13 (cancel-after-create): in the success TXN2, if CancellationRequestedAt != null, route
///     to CompensationRequired instead of Booked so Compensate cancels the created booking.
///
/// Idempotent recovery shortcut (§12.6): if the latest attempt already Succeeded/Reconciled,
/// just ensure Booked and return Terminal without re-calling the provider.
///
/// Inv-7 guard: if the latest attempt is Dispatching or Ambiguous (the effect is unknown),
/// transition to Reconciling and return Continue so the driver dispatches
/// <see cref="Reconcile.ReconcileOperationCommand"/> before any re-create (§16.2 lookup order).
///
/// Poison/DLQ (Inv-14): unexpected adapter exceptions propagate to AdvanceBookingOperationCommandHandler,
/// which catches <see cref="Domain.Exceptions.PoisonMessageException"/> /
/// <see cref="Domain.Exceptions.InvalidStateTransitionException"/> and handles quarantine there.
///
/// First-party capacity finalization (spec 07 §6.1, §6.2, FIX 2+3+4+8):
///   <see cref="ICapacityReservation.FinalizeAgainstOperationAsync"/> is the SINGLE decision point:
///   <list type="bullet">
///     <item>Finalized (slot moved or already moved) → proceed to Booked (idempotent).</item>
///     <item>HoldNoLongerValid (hold expired/released — capacity gone but provider effect exists) →
///       Finalizing→CompensationRequired (reason "hold_no_longer_valid") so Compensate cancels the
///       provider booking and keeps capacity consistent. Do NOT route to Reconciling. Do NOT emit
///       booking_confirmed.</item>
///     <item>NotTracked (no hold ever created) → non-tracked op, proceed to Booked unchanged.</item>
///   </list>
///   Called inside TXN2 (same txn as Booked transition) and in the idempotent recovery shortcut.
/// </summary>
public sealed class FinalizeBookingCommandHandler(
    IBookingOperationRepository opRepo,
    IWorkflowStepRepository stepRepo,
    IWorkflowAttemptRepository attemptRepo,
    IBookingIntentRepository intentRepo,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    IBookingProviderAdapter adapter,
    IBookingRepository bookingRepo,
    IProviderCircuitBreaker circuitBreaker,
    RetryClassifier classifier,
    IRetryPolicy retryPolicy,
    RetryScheduler retryScheduler,
    IUnitOfWork uow,
    ICapacityReservation capacityReservation,
    Metrics metrics)
    : IRequestHandler<FinalizeBookingCommand, StepOutcome>
{
    private const string StepType = "finalize";
    private const string ProviderAccount = "default";

    public async Task<StepOutcome> Handle(FinalizeBookingCommand command, CancellationToken ct)
    {
        var op = await opRepo.GetAsync(command.OperationId, ct);

        if (op is null || op.TenantId != command.TenantId)
            return StepOutcome.Wait;

        if (op.Status is not (OperationStatus.Held or OperationStatus.Finalizing))
            return StepOutcome.Continue;

        var tenantId = command.TenantId;
        var operationId = command.OperationId;

        // Stable idempotency key for this step+revision (Inv-2/3: same across retries).
        var providerKey = ProviderIdempotencyKey.Derive(operationId, StepType, op.IntentRevision);

        // Load the intent revision for request_hash and canonical payload.
        var revision = await intentRepo.GetRevisionAsync(tenantId, op.IntentId, op.IntentRevision, ct);
        var requestHash = revision?.PayloadHash ?? string.Empty;
        var canonicalPayload = revision?.CanonicalPayload ?? "{}";

        // --- Idempotent recovery shortcut ---
        // If a prior attempt already succeeded (crash-before-TXN2-commit recovery), skip the
        // external call and just ensure the operation is Booked.
        // If a prior attempt is Dispatching or Ambiguous (we MAY have sent a request — Inv-7),
        // do NOT create a new booking. Transition to Reconciling and let the driver dispatch
        // ReconcileOperationCommand to probe via the §16.2 lookup order before any re-create.
        var latest = await attemptRepo.GetLatestForStepAsync(tenantId, operationId, StepType, ct);
        if (latest is { Outcome: AttemptOutcome.Succeeded or AttemptOutcome.Reconciled } && latest.ProviderRef is not null)
        {
            // Ensure op is Booked; may be a no-op if already Booked.
            var fresh = await opRepo.GetAsync(operationId, ct);
            if (fresh is not null && fresh.Status == OperationStatus.Finalizing)
            {
                // Track whether a HoldNoLongerValid race was detected in the recovery shortcut
                // so we can return Continue (→Reconciling) instead of Terminal after the txn.
                var recoveryHoldExpired = false;

                await uow.ExecuteInTransactionAsync(async ct2 =>
                {
                    var current2 = await opRepo.GetAsync(operationId, ct2);
                    if (current2 is null || current2.Status != OperationStatus.Finalizing)
                        return;

                    // §6.1/§6.2 recovery (FIX 2+3+4+8): FinalizeAgainstOperationAsync is the SINGLE
                    // decision point. Finalized (active→finalized or already finalized idempotent) →
                    // fall through to Booked. HoldNoLongerValid → CompensationRequired (capacity gone,
                    // Compensate will cancel the provider booking). NotTracked → non-tracked op,
                    // fall through to Booked.
                    var finalizeOutcome2 = await capacityReservation.FinalizeAgainstOperationAsync(
                        tenantId, operationId, "create", ct2);

                    if (finalizeOutcome2 == FinalizeOutcome.HoldNoLongerValid)
                    {
                        // Hold expired/released and provider effect exists — route to
                        // CompensationRequired (not Reconciling) so Compensate cancels the booking.
                        var holdExpiredRows = await opRepo.TryTransitionStatusAsync(
                            tenantId, operationId, current2.Version,
                            OperationStatus.Finalizing, OperationStatus.CompensationRequired,
                            reason: "hold_no_longer_valid", ct: ct2);

                        if (holdExpiredRows > 0)
                        {
                            recoveryHoldExpired = true;
                            await auditRepo.AddAsync(new AuditEvent
                            {
                                TenantId = tenantId,
                                EventId = $"evt_{Guid.NewGuid():N}",
                                EventType = "operation.hold_no_longer_valid",
                                Actor = "system",
                                SubjectRef = operationId,
                                CorrelationId = operationId,
                                OperationId = operationId,
                                Metadata = JsonSerializer.Serialize(new { hold_id = (string?)null }),
                                OccurredAt = DateTimeOffset.UtcNow,
                            }, ct2);
                        }
                        return; // do NOT book; Compensate will cancel the provider booking
                    }
                    // FinalizeOutcome.Finalized or NotTracked → fall through to Booked transition below.

                    var bookedRows = await opRepo.TryTransitionStatusAsync(
                        tenantId, operationId, current2.Version,
                        OperationStatus.Finalizing, OperationStatus.Booked,
                        reason: "booked", bookingId: latest.ProviderRef, ct: ct2);

                    if (bookedRows > 0)
                    {
                        await bookingRepo.MaterializeBookedAsync(
                            BuildBookingAggregate(tenantId, current2, latest.ProviderRef, canonicalPayload, now: DateTimeOffset.UtcNow),
                            ct2);
                    }
                }, ct);

                if (recoveryHoldExpired)
                    return StepOutcome.Continue;
            }
            return StepOutcome.Terminal;
        }

        // Inv-7: if op is in Finalizing AND the latest attempt is Dispatching or Ambiguous, we MAY
        // have already sent a request to the provider. Do NOT create a new booking. Transition to
        // Reconciling so the driver dispatches ReconcileOperationCommand (§16.2 lookup order) before
        // any re-create.
        //
        // NOTE: this guard only applies when op.Status == Finalizing (not Held). When op.Status ==
        // Held after a Reconciling→Held no-effect path, the reconcile handler has already proven
        // no effect exists — fall through to TXN1 which will create a fresh attempt row.
        if (op.Status == OperationStatus.Finalizing
            && latest is { Outcome: AttemptOutcome.Dispatching or AttemptOutcome.Ambiguous })
        {
            var currentOp = await opRepo.GetAsync(operationId, ct);
            if (currentOp is not null && currentOp.Status == OperationStatus.Finalizing)
            {
                await opRepo.TryTransitionStatusAsync(
                    tenantId, operationId, currentOp.Version,
                    OperationStatus.Finalizing, OperationStatus.Reconciling,
                    reason: "reconcile_ambiguous",
                    ct: ct);
            }
            // Return Continue: driver reloads → op.Status == Reconciling → ReconcileOperationCommand.
            return StepOutcome.Continue;
        }

        // ─────────────────────────────────────────────────────────────
        // FM#13 (cancel-before-create): if cancellation was requested AND no attempt has been
        // dispatched/created yet (latest is null or still Planned), route directly to
        // CompensationRequired with reason "cancelled_before_create".  The Compensate handler
        // will see providerRef == null and transition straight to FailedFinal.
        //
        // If the latest attempt is Dispatching or Ambiguous, the effect is unknown — fall through
        // to the Inv-7 reconcile path above (not special-cased here).
        // ─────────────────────────────────────────────────────────────
        if (op.CancellationRequestedAt.HasValue
            && (latest is null || latest.Outcome == AttemptOutcome.Planned))
        {
            var freshOpCancel = await opRepo.GetAsync(operationId, ct);
            if (freshOpCancel is not null
                && freshOpCancel.Status is OperationStatus.Held or OperationStatus.Finalizing)
            {
                await uow.ExecuteInTransactionAsync(async ct2 =>
                {
                    var opInTxn = await opRepo.GetAsync(operationId, ct2);
                    if (opInTxn is null)
                        return;

                    // FIX (C3): Re-read the latest attempt INSIDE the transaction so we don't act
                    // on a stale snapshot.  If a concurrent dispatch has already moved the attempt
                    // to Dispatching (or beyond), the effect is unknown — fall through to Continue
                    // so the Inv-7 reconcile guard / normal path handles it.  Only proceed with the
                    // cancel-before-create shortcut when the attempt is still null or Planned.
                    var latestInTxn = await attemptRepo.GetLatestForStepAsync(
                        tenantId, operationId, StepType, ct2);
                    if (latestInTxn is not null
                        && latestInTxn.Outcome is AttemptOutcome.Dispatching
                            or AttemptOutcome.Ambiguous
                            or AttemptOutcome.Succeeded
                            or AttemptOutcome.Reconciled)
                    {
                        // A concurrent dispatch is in-flight or already completed — do not route
                        // to CompensationRequired here.  Return without transition; caller returns
                        // Continue so the driver re-evaluates (Inv-7 / success shortcut will fire).
                        return;
                    }

                    // Ensure we are in Finalizing before transitioning to CompensationRequired.
                    var currentStatus = opInTxn.Status;
                    if (currentStatus == OperationStatus.Held)
                    {
                        var heldRows = await opRepo.TryTransitionStatusAsync(
                            tenantId, operationId, opInTxn.Version,
                            OperationStatus.Held, OperationStatus.Finalizing,
                            reason: "finalizing", ct: ct2);
                        if (heldRows == 0)
                            return; // lost the race; reload
                        // Re-read version after Held→Finalizing.
                        opInTxn = await opRepo.GetAsync(operationId, ct2);
                        if (opInTxn is null)
                            return;
                    }

                    if (opInTxn.Status != OperationStatus.Finalizing)
                        return;

                    await opRepo.TryTransitionStatusAsync(
                        tenantId, operationId, opInTxn.Version,
                        OperationStatus.Finalizing, OperationStatus.CompensationRequired,
                        reason: "cancelled_before_create", ct: ct2);
                }, ct);
            }

            return StepOutcome.Continue;
        }

        // ─────────────────────────────────────────────────────────────
        // TXN1: claim the operation into Finalizing, create/load the step, insert a Planned attempt.
        // ─────────────────────────────────────────────────────────────
        WorkflowAttempt? attempt = null;
        WorkflowStep? finalizeStep = null;
        var txn1Aborted = false;

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            var current = await opRepo.GetAsync(operationId, ct2);
            if (current is null || current.TenantId != tenantId)
            {
                txn1Aborted = true;
                return;
            }

            // Transition Held → Finalizing only if currently Held.
            if (current.Status == OperationStatus.Held)
            {
                var rows = await opRepo.TryTransitionStatusAsync(
                    tenantId, operationId, current.Version,
                    OperationStatus.Held, OperationStatus.Finalizing,
                    reason: "finalizing", ct: ct2);
                if (rows == 0)
                {
                    txn1Aborted = true; // lost the Held→Finalizing race; caller reloads + re-evaluates
                    return;
                }
            }
            else if (current.Status != OperationStatus.Finalizing)
            {
                txn1Aborted = true;
                return;
            }

            // Create-or-load the finalize WorkflowStep (store reference so circuit/retry have the id).
            var now = DateTimeOffset.UtcNow;
            var existingStep = await stepRepo.GetByOperationAndTypeAsync(tenantId, operationId, StepType, ct2);
            if (existingStep is null)
            {
                var newStep = new WorkflowStep
                {
                    TenantId = tenantId,
                    WorkflowStepId = $"ws_{Guid.NewGuid():N}",
                    OperationId = operationId,
                    StepType = StepType,
                    Status = WorkflowStepStatus.InProgress,
                    ProviderIdempotencyKey = providerKey,
                    AttemptCount = 1,
                    Version = 0,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                await stepRepo.AddAsync(newStep, ct2);
                finalizeStep = newStep;
            }
            else
            {
                if (existingStep.Status == WorkflowStepStatus.Pending)
                {
                    await stepRepo.TryTransitionStatusAsync(
                        tenantId, existingStep.WorkflowStepId, existingStep.Version,
                        WorkflowStepStatus.Pending, WorkflowStepStatus.InProgress, ct2);
                }
                finalizeStep = existingStep;
            }

            // Insert a Planned attempt row.
            var latestAttemptNumber = (await attemptRepo.GetLatestForStepAsync(tenantId, operationId, StepType, ct2))?.AttemptNumber ?? 0;
            attempt = new WorkflowAttempt
            {
                TenantId = tenantId,
                AttemptId = $"att_{Guid.NewGuid():N}",
                OperationId = operationId,
                WorkflowStep = StepType,
                ProviderIdempotencyKey = providerKey,
                ProviderName = adapter.ProviderName,
                ProviderAccount = ProviderAccount,
                RequestHash = requestHash,
                AttemptNumber = latestAttemptNumber + 1,
                StartedAt = now,
                Outcome = AttemptOutcome.Planned,
                RequestMeta = "{}",
                ResponseMeta = "{}",
            };
            await attemptRepo.AddAsync(attempt, ct2);
        }, ct);

        if (txn1Aborted || attempt is null || finalizeStep is null)
            return StepOutcome.Continue; // reload and re-evaluate

        // ─────────────────────────────────────────────────────────────
        // Circuit check (§15.4 — do NOT flood an unhealthy provider).
        // ─────────────────────────────────────────────────────────────
        if (circuitBreaker.IsOpen(adapter.ProviderName, ProviderAccount))
        {
            var now = DateTimeOffset.UtcNow;

            // Compute a durable retry time before opening the transaction (pure, no I/O).
            // Fall back to 30 s if budget is already exhausted.
            var nextEligible = retryPolicy.NextEligibleAt(
                StepRetryBudget.ForCreateBooking,
                attempt.AttemptNumber,
                finalizeStep.CreatedAt,
                now) ?? now.AddSeconds(30);

            // FIX (X3): Abandon the attempt AND schedule the durable retry INSIDE the same
            // transaction so a crash between the two writes cannot leave the op in Finalizing with
            // a retryable attempt but no scheduled job / NotBefore gate.
            // RetryScheduler uses the same scoped DbContext — its writes flush into this txn.
            await uow.ExecuteInTransactionAsync(async ct2 =>
            {
                attempt.Outcome = AttemptOutcome.Abandoned;
                attempt.CompletedAt = now;
                await attemptRepo.UpdateAsync(attempt, ct2);

                await retryScheduler.ScheduleAsync(
                    tenantId, operationId, finalizeStep.WorkflowStepId, StepType, nextEligible, ct2);
            }, ct);

            return StepOutcome.Reschedule;
        }

        // ─────────────────────────────────────────────────────────────
        // Mark Dispatching (best-effort, before the external call).
        // ─────────────────────────────────────────────────────────────
        attempt.Outcome = AttemptOutcome.Dispatching;
        await attemptRepo.UpdateAsync(attempt, ct);

        // ─────────────────────────────────────────────────────────────
        // External call — unexpected exceptions propagate to the driver (AdvanceBookingOperationCommandHandler),
        // which catches PoisonMessageException / InvalidStateTransitionException and handles DLQ quarantine
        // (Inv-14). Non-defect failures (transient/ambiguous/etc.) are handled below in TXN2.
        // ─────────────────────────────────────────────────────────────
        var result = await adapter.CreateBookingAsync(
            new ProviderBookingRequest(tenantId, operationId, ProviderAccount, providerKey, requestHash, canonicalPayload),
            ct);

        // ─────────────────────────────────────────────────────────────
        // TXN2: persist the outcome.
        // ─────────────────────────────────────────────────────────────
        if (result.Kind is ProviderResultKind.Created or ProviderResultKind.AlreadyExists)
        {
            // FM#13 (cancel-after-create): re-read the op inside TXN2 to check whether a
            // cancellation was requested AFTER we dispatched the external call.  If so, route to
            // CompensationRequired (reason "cancelled_after_create") instead of Booked.
            // The Compensate handler will call CancelBookingAsync to undo the booking.
            // Do NOT emit booking_confirmed in this case.
            await uow.ExecuteInTransactionAsync(async ct2 =>
            {
                var now = DateTimeOffset.UtcNow;

                // Update attempt → Succeeded (the booking WAS created, ref recorded).
                attempt.Outcome = AttemptOutcome.Succeeded;
                attempt.ProviderRef = result.ProviderReference;
                attempt.CompletedAt = now;
                await attemptRepo.UpdateAsync(attempt, ct2);

                circuitBreaker.RecordSuccess(adapter.ProviderName, ProviderAccount);
                if (result.Kind == ProviderResultKind.AlreadyExists)
                    metrics.FulfillmentProviderIdempotencyConflictTotal.Add(1, new KeyValuePair<string, object?>(MetricNames.Tags.Provider, adapter.ProviderName));

                // Transition finalize step InProgress → Succeeded.
                var step = await stepRepo.GetByOperationAndTypeAsync(tenantId, operationId, StepType, ct2);
                if (step is not null && step.Status == WorkflowStepStatus.InProgress)
                {
                    await stepRepo.TryTransitionStatusAsync(
                        tenantId, step.WorkflowStepId, step.Version,
                        WorkflowStepStatus.InProgress, WorkflowStepStatus.Succeeded, ct2);
                }

                // Re-read the op to detect a cancellation race (FM#13).
                var currentOp = await opRepo.GetAsync(operationId, ct2);
                if (currentOp is null || currentOp.Status != OperationStatus.Finalizing)
                    return;

                if (currentOp.CancellationRequestedAt.HasValue)
                {
                    // Cancellation was requested after the booking was created — route to
                    // CompensationRequired so the Compensate handler cancels the booking.
                    var cancelRows = await opRepo.TryTransitionStatusAsync(
                        tenantId, operationId, currentOp.Version,
                        OperationStatus.Finalizing, OperationStatus.CompensationRequired,
                        reason: "cancelled_after_create", ct: ct2);

                    if (cancelRows > 0)
                    {
                        await auditRepo.AddAsync(new AuditEvent
                        {
                            TenantId = tenantId,
                            EventId = $"evt_{Guid.NewGuid():N}",
                            EventType = "operation.cancellation_after_create",
                            Actor = "system",
                            SubjectRef = operationId,
                            CorrelationId = operationId,
                            OperationId = operationId,
                            Metadata = JsonSerializer.Serialize(new
                            {
                                provider_ref = result.ProviderReference,
                                reason = "cancelled_after_create",
                            }),
                            OccurredAt = now,
                        }, ct2);
                    }

                    return; // do NOT emit booking_confirmed
                }

                // No cancellation: normal Finalizing → Booked path (Inv-8 order: Booked + outbox atomically).
                // §6.1/§6.2 (FIX 2+3+4+8): FinalizeAgainstOperationAsync is the SINGLE decision point.
                // Finalized (slot moved or already moved idempotent) → fall through to Booked.
                // HoldNoLongerValid (hold expired/released, capacity gone) → CompensationRequired so
                //   Compensate cancels the provider booking (DO NOT route to Reconciling — that
                //   would confirm a booking with no capacity backing). Do NOT emit booking_confirmed.
                // NotTracked (no hold ever created) → non-tracked op, fall through to Booked.
                var finalizeOutcome = await capacityReservation.FinalizeAgainstOperationAsync(
                    tenantId, operationId, "create", ct2);

                if (finalizeOutcome == FinalizeOutcome.HoldNoLongerValid)
                {
                    // Hold expired/released but provider created the booking — capacity gone.
                    // Route to CompensationRequired (not Reconciling) so Compensate cancels the
                    // provider booking and keeps capacity consistent. Do NOT emit booking_confirmed.
                    var holdRows = await opRepo.TryTransitionStatusAsync(
                        tenantId, operationId, currentOp.Version,
                        OperationStatus.Finalizing, OperationStatus.CompensationRequired,
                        reason: "hold_no_longer_valid", ct: ct2);

                    if (holdRows > 0)
                    {
                        await auditRepo.AddAsync(new AuditEvent
                        {
                            TenantId = tenantId,
                            EventId = $"evt_{Guid.NewGuid():N}",
                            EventType = "operation.hold_no_longer_valid",
                            Actor = "system",
                            SubjectRef = operationId,
                            CorrelationId = operationId,
                            OperationId = operationId,
                            Metadata = JsonSerializer.Serialize(new
                            {
                                hold_id = (string?)null,
                                provider_ref = result.ProviderReference,
                            }),
                            OccurredAt = now,
                        }, ct2);
                    }

                    return; // do NOT emit booking_confirmed; driver dispatches Compensate
                }
                // FinalizeOutcome.Finalized or NotTracked → fall through to Finalizing→Booked below.

                if (string.IsNullOrWhiteSpace(result.ProviderReference))
                    return;

                var rows = await opRepo.TryTransitionStatusAsync(
                    tenantId, operationId, currentOp.Version,
                    OperationStatus.Finalizing, OperationStatus.Booked,
                    reason: "booked", bookingId: result.ProviderReference, ct: ct2);

                if (rows > 0)
                {
                    await bookingRepo.MaterializeBookedAsync(
                        BuildBookingAggregate(tenantId, currentOp, result.ProviderReference, canonicalPayload, now),
                        ct2);

                    // Inv-8: outcome outbox row in the SAME transaction as the Booked transition.
                    // Spec 11 §5.3: a pre-order op signals the saga (which emits the one combined
                    // customer outcome) instead of notifying the customer directly here.
                    await outboxRepo.AddAsync(new OutboxMessage
                    {
                        TenantId = tenantId,
                        MessageId = $"obx_{Guid.NewGuid():N}",
                        OperationId = operationId,
                        MessageType = Cordia.Application.Handlers.Preorder.PreorderSagaMessages.ConfirmedOutcomeType(currentOp.IsPreorder),
                        CorrelationId = operationId,
                        Payload = JsonSerializer.Serialize(new
                        {
                            operation_id = operationId,
                            booking_id = result.ProviderReference,
                            tenant_id = tenantId,
                        }),
                        OccurredAt = now,
                    }, ct2);

                    await auditRepo.AddAsync(new AuditEvent
                    {
                        TenantId = tenantId,
                        EventId = $"evt_{Guid.NewGuid():N}",
                        EventType = "operation.booked",
                        Actor = "system",
                        SubjectRef = operationId,
                        CorrelationId = operationId,
                        OperationId = operationId,
                        Metadata = JsonSerializer.Serialize(new
                        {
                            booking_id = result.ProviderReference,
                            provider = adapter.ProviderName,
                        }),
                        OccurredAt = now,
                    }, ct2);
                    metrics.FulfillmentAcceptedToBookedMs.Record((now - op.CreatedAt).TotalMilliseconds, new KeyValuePair<string, object?>(MetricNames.Tags.Provider, adapter.ProviderName));
                    metrics.FulfillmentOperationTerminalTotal.Add(1, new KeyValuePair<string, object?>(MetricNames.Tags.State, "booked"));
                }
            }, ct);

            // Re-read op to detect routing decisions made inside TXN2.
            // This success branch (provider Created/AlreadyExists) only ever transitions to
            // CompensationRequired — via FM#13 cancellation-after-create OR HoldNoLongerValid (FIX 2,
            // Compensate cancels the provider booking) — or to Booked. The Reconciling check is kept
            // defensively (a future routing change here would still yield the correct Continue).
            // Continue lets the driver dispatch the appropriate next command.
            var postTxnOp = await opRepo.GetAsync(operationId, ct);
            if (postTxnOp?.Status is OperationStatus.CompensationRequired or OperationStatus.Reconciling)
                return StepOutcome.Continue;

            return StepOutcome.Terminal;
        }

        // TXN2 (ambiguous): the effect is unknown — transition op to Reconciling (Inv-7) so the
        // driver dispatches ReconcileOperationCommand (§16.2 key→reference→status) before any re-create.
        if (result.Kind == ProviderResultKind.Ambiguous)
        {
            await uow.ExecuteInTransactionAsync(async ct2 =>
            {
                attempt.Outcome = AttemptOutcome.Ambiguous;
                attempt.RetryClassification = classifier.Classify(result).Classification;
                attempt.ErrorCode = result.ErrorCode;
                attempt.CompletedAt = DateTimeOffset.UtcNow;
                await attemptRepo.UpdateAsync(attempt, ct2);

                // Transition op Finalizing → Reconciling (version-guarded).
                var currentOp = await opRepo.GetAsync(operationId, ct2);
                if (currentOp is not null && currentOp.Status == OperationStatus.Finalizing)
                {
                    await opRepo.TryTransitionStatusAsync(
                        tenantId, operationId, currentOp.Version,
                        OperationStatus.Finalizing, OperationStatus.Reconciling,
                        reason: "reconcile_ambiguous",
                        ct: ct2);
                }

                circuitBreaker.RecordFailure(adapter.ProviderName, ProviderAccount);
                metrics.FulfillmentAmbiguousOutcomeTotal.Add(1);
            }, ct);

            // Continue: driver reloads → op.Status == Reconciling → ReconcileOperationCommand.
            return StepOutcome.Continue;
        }

        // TXN2 (transient): full-jitter retry or budget-exhausted → Reconciling (FM#11 path).
        if (result.Kind == ProviderResultKind.Transient)
        {
            var now = DateTimeOffset.UtcNow;
            // Compute next eligible time before opening the transaction (pure, no I/O).
            var next = retryPolicy.NextEligibleAt(
                StepRetryBudget.ForCreateBooking,
                attempt.AttemptNumber,
                finalizeStep.CreatedAt,
                now);

            await uow.ExecuteInTransactionAsync(async ct2 =>
            {
                attempt.Outcome = AttemptOutcome.TransientFailure;
                attempt.RetryClassification = classifier.Classify(result).Classification;
                attempt.ErrorCode = result.ErrorCode;
                attempt.CompletedAt = now;
                attempt.NextEligibleAt = next;
                await attemptRepo.UpdateAsync(attempt, ct2);

                circuitBreaker.RecordFailure(adapter.ProviderName, ProviderAccount);

                // Record elapsed for ALL transient failures (including budget-exhausted ones) so the
                // retry-backlog SLA metric captures the full latency distribution (§25.2).
                metrics.FulfillmentRetryElapsedMs.Record((now - finalizeStep.CreatedAt).TotalMilliseconds);

                if (next == null)
                {
                    // Budget exhausted (FM#11): route Finalizing → Reconciling.
                    // ReconcileOperationCommand (no-effect proven + budget exhausted) → ManualReview.
                    var currentOp = await opRepo.GetAsync(operationId, ct2);
                    if (currentOp is not null && currentOp.Status == OperationStatus.Finalizing)
                    {
                        await opRepo.TryTransitionStatusAsync(
                            tenantId, operationId, currentOp.Version,
                            OperationStatus.Finalizing, OperationStatus.Reconciling,
                            reason: "retry_budget_exhausted",
                            ct: ct2);
                    }
                }
                else
                {
                    // FIX (X3): Schedule the durable retry INSIDE the same transaction as the
                    // attempt-outcome write.  RetryScheduler uses the same scoped DbContext and
                    // does not open its own txn — its AddAsync/SetNotBeforeAsync flush into this
                    // ambient transaction, making the job insert and NotBefore gate atomic with
                    // the TransientFailure attempt row.
                    await retryScheduler.ScheduleAsync(
                        tenantId, operationId, finalizeStep.WorkflowStepId, StepType, next.Value, ct2);
                    metrics.FulfillmentRetryAttemptTotal.Add(1, new KeyValuePair<string, object?>(MetricNames.Tags.Category, attempt.RetryClassification?.ToString() ?? "transient"));
                }
            }, ct);

            if (next != null)
                return StepOutcome.Reschedule;

            // Budget exhausted → op is now Reconciling; driver loops immediately into Reconcile.
            return StepOutcome.Continue;
        }

        // TXN2 (NoCapacity at finalize): anomalous — §11 has NO Finalizing→AlternativesRequired edge.
        // Route to Reconciling (reason "unexpected_no_capacity"); reconcile proves no-effect then
        // exhausts the budget → ManualReview.
        if (result.Kind == ProviderResultKind.NoCapacity)
        {
            await uow.ExecuteInTransactionAsync(async ct2 =>
            {
                attempt.Outcome = AttemptOutcome.TransientFailure;
                attempt.RetryClassification = RetryClassification.NoCapacity;
                attempt.ErrorCode = result.ErrorCode;
                attempt.CompletedAt = DateTimeOffset.UtcNow;
                await attemptRepo.UpdateAsync(attempt, ct2);

                var currentOp = await opRepo.GetAsync(operationId, ct2);
                if (currentOp is not null && currentOp.Status == OperationStatus.Finalizing)
                {
                    await opRepo.TryTransitionStatusAsync(
                        tenantId, operationId, currentOp.Version,
                        OperationStatus.Finalizing, OperationStatus.Reconciling,
                        reason: "unexpected_no_capacity",
                        ct: ct2);
                }

                circuitBreaker.RecordFailure(adapter.ProviderName, ProviderAccount);
            }, ct);

            return StepOutcome.Continue;
        }

        // TXN2 (Rejected / PermanentError): persist attempt outcome + transition
        // Finalizing → CompensationRequired.  The driver will dispatch CompensateOperationCommand.
        //
        // FIX (X1): AuthError and PaymentActionRequired are NOT permanent booking failures.
        //   AuthError = our credentials/ops issue (classifier: ManualReview).
        //   PaymentActionRequired = action needed from the user/ops (classifier: AwaitUser).
        //   §11 forbids Finalizing→AwaitingUserAction and Finalizing→ManualReview directly.
        //   Route them to Reconciling instead — the reconcile handler's no-effect + escalation
        //   path will transition to ManualReview, which is the correct destination for an ops
        //   or action-required issue.  Only Rejected/PermanentError go to CompensationRequired.
        if (result.Kind is ProviderResultKind.AuthError or ProviderResultKind.PaymentActionRequired)
        {
            await uow.ExecuteInTransactionAsync(async ct2 =>
            {
                attempt.Outcome = AttemptOutcome.Abandoned;
                attempt.RetryClassification = classifier.Classify(result).Classification;
                attempt.ErrorCode = result.ErrorCode;
                attempt.CompletedAt = DateTimeOffset.UtcNow;
                await attemptRepo.UpdateAsync(attempt, ct2);

                circuitBreaker.RecordFailure(adapter.ProviderName, ProviderAccount);

                // Finalizing → Reconciling (version-guarded).  The reconcile handler will
                // escalate to ManualReview once it proves no effect and the budget is consumed.
                var currentOp = await opRepo.GetAsync(operationId, ct2);
                if (currentOp is not null && currentOp.Status == OperationStatus.Finalizing)
                {
                    var reason = result.Kind == ProviderResultKind.AuthError
                        ? "finalize_auth_error"
                        : "finalize_provider_action_required";
                    await opRepo.TryTransitionStatusAsync(
                        tenantId, operationId, currentOp.Version,
                        OperationStatus.Finalizing, OperationStatus.Reconciling,
                        reason: reason, ct: ct2);
                }
            }, ct);

            // Continue: driver reloads → op.Status == Reconciling → ReconcileOperationCommand.
            return StepOutcome.Continue;
        }

        // TXN2 (Rejected / PermanentError): persist attempt outcome + transition
        // Finalizing → CompensationRequired.  The driver will dispatch CompensateOperationCommand.
        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            attempt.Outcome = result.Kind switch
            {
                ProviderResultKind.Rejected => AttemptOutcome.Rejected,
                ProviderResultKind.PermanentError => AttemptOutcome.Rejected,
                _ => AttemptOutcome.Abandoned,
            };
            attempt.RetryClassification = classifier.Classify(result).Classification;
            attempt.ErrorCode = result.ErrorCode;
            attempt.CompletedAt = DateTimeOffset.UtcNow;
            await attemptRepo.UpdateAsync(attempt, ct2);

            circuitBreaker.RecordFailure(adapter.ProviderName, ProviderAccount);

            // Finalizing → CompensationRequired (version-guarded).
            var currentOp = await opRepo.GetAsync(operationId, ct2);
            if (currentOp is not null && currentOp.Status == OperationStatus.Finalizing)
            {
                await opRepo.TryTransitionStatusAsync(
                    tenantId, operationId, currentOp.Version,
                    OperationStatus.Finalizing, OperationStatus.CompensationRequired,
                    reason: "finalize_permanent_error", ct: ct2);
            }
        }, ct);

        // Continue: driver reloads → op.Status == CompensationRequired → CompensateOperationCommand.
        return StepOutcome.Continue;
    }

    private static Booking BuildBookingAggregate(
        string tenantId,
        BookingOperation operation,
        string providerReference,
        string canonicalPayload,
        DateTimeOffset now)
    {
        var bookingReference = ReadFirstString(canonicalPayload, "booking_reference", "bookingReference", "reference")
            ?? providerReference;
        var customerRef = ReadFirstString(
                canonicalPayload,
                "customer_id",
                "customerId",
                "caller_id",
                "callerId",
                "phone_e164",
                "phoneE164",
                "contact_phone",
                "contactPhone",
                "email",
                "contact_email",
                "contactEmail")
            ?? providerReference;

        return new Booking
        {
            TenantId = tenantId,
            BookingId = providerReference,
            BookingReference = bookingReference,
            ProviderReference = providerReference,
            Version = 0,
            LifecycleStatus = BookingLifecycleStatus.Confirmed,
            CustomerId = customerRef,
            CreatedAt = operation.CreatedAt,
            UpdatedAt = now,
        };
    }

    private static string? ReadFirstString(string json, params string[] propertyNames)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ReadFirstString(doc.RootElement, propertyNames);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadFirstString(JsonElement element, IReadOnlyCollection<string> propertyNames)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (propertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    return StringValue(property.Value);

                var nested = ReadFirstString(property.Value, propertyNames);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = ReadFirstString(item, propertyNames);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }

        return null;
    }

    private static string? StringValue(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(element.GetString()) ? null : element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null,
        };
}
