using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Observability;
using Cordia.Infrastructure.Observability;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.CancelBooking;

/// <summary>
/// Fulfils a cancellation operation (spec 08 §5.3, spec 06 §11, §12.6).
///
/// Before this step existed, a cancel operation was driven through the generic create pipeline
/// and called <c>CreateBookingAsync</c> — producing a <b>second booking</b> and leaving the
/// original stuck in <c>cancellation_pending</c>. The driver now routes cancel-type operations
/// here at <c>Finalizing</c>.
///
/// Protocol (mirrors <c>CompensateOperationCommandHandler</c>, the proven staged-call shape):
///   1. Load the operation; guard tenant, status == Finalizing, type == Cancel.
///   2. Load the target booking from <c>op.BookingId</c>.
///      Already <c>Cancelled</c> ⇒ succeed <b>without re-invoking the provider</b> (§9.4.4).
///   3. Create-or-load the "cancel" workflow step (idempotent, Inv-5).
///   4. Call <c>adapter.CancelBookingAsync</c> <b>outside</b> the transaction (§12.6).
///   5a. Created / AlreadyExists / NotFound — cancel acknowledged, or the booking was already
///       gone at the provider — one transaction: step → Succeeded; booking → Cancelled
///       (version-guarded); capacity given back; operation Finalizing → Booked
///       (reason "cancelled"); outbox <c>notification.cancel_confirmed.v1</c>; audit.
///   5b. Ambiguous / Transient / PermanentError / AuthError ⇒ Finalizing → Reconciling (Inv-7).
///       Never a blind retry — the reconciler establishes what actually happened first.
///
/// <para><b>Why the operation ends at <c>Booked</c>:</b> D1 defines no <c>cancelled</c>
/// operation status, and D2's cross-service projection has none either. <c>Booked</c> is the
/// generic "this operation succeeded" terminal; the <i>booking</i> carries the cancellation.
/// Callers phrase the outcome by operation type — the console does exactly that.</para>
///
/// <para><b>Inv-8:</b> the booking transition and the notification outbox row commit in the same
/// transaction, so a customer can never be told "cancelled" before it is true.</para>
/// </summary>
public sealed class CancelBookingCommandHandler(
    IBookingOperationRepository opRepo,
    IBookingRepository bookingRepo,
    IWorkflowStepRepository stepRepo,
    IBookingProviderAdapter adapter,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    ICapacityReservation capacityReservation,
    IUnitOfWork uow,
    Metrics metrics)
    : IRequestHandler<CancelBookingCommand, StepOutcome>
{
    private const string CancelStepType = "cancel";
    private const string ProviderAccount = "default";

    /// <summary>
    /// Re-read-and-retry budget for the version-guarded booking transition. Bounded so a row
    /// under sustained concurrent write rolls back and redelivers instead of spinning.
    /// </summary>
    private const int BookingTransitionAttempts = 3;

    /// <summary>Customer-facing "your cancellation is confirmed" event (spec 10 catalogue).</summary>
    public const string CancelConfirmedEventType = "notification.cancel_confirmed.v1";

    public async Task<StepOutcome> Handle(CancelBookingCommand command, CancellationToken ct)
    {
        var op = await opRepo.GetAsync(command.OperationId, ct);

        if (op is null || op.TenantId != command.TenantId)
            return StepOutcome.Wait;

        if (op.Status != OperationStatus.Finalizing)
            return StepOutcome.Continue;

        if (op.OperationType != Domain.Generated.OperationTypeEnum.Cancel)
            return StepOutcome.Continue;

        var tenantId = command.TenantId;
        var operationId = command.OperationId;

        // A cancellation with no target is a defect, not a transient failure: retrying can never
        // discover one. Poison it so it is quarantined and surfaced (Inv-14) rather than looping.
        if (string.IsNullOrWhiteSpace(op.BookingId))
            throw new PoisonMessageException(
                $"Cancel operation '{operationId}' carries no target booking id.");

        var booking = await bookingRepo.GetByIdAsync(tenantId, op.BookingId, ct);
        if (booking is null)
            throw new PoisonMessageException(
                $"Cancel operation '{operationId}' targets booking '{op.BookingId}', which does not exist.");

        // Spec 08 §9.4.4: a cancellation against an already-cancelled booking settles from
        // current state and must NOT re-invoke the provider.
        var alreadyCancelled = booking.LifecycleStatus == BookingLifecycleStatus.Cancelled;

        var cancelStep = await CreateOrLoadStepAsync(tenantId, operationId, ct);
        if (cancelStep is null)
            return StepOutcome.Continue;

        var providerRef = booking.ProviderReference;

        // ─────────────────────────────────────────────────────────────
        // External call — OUTSIDE the transaction (§12.6). Skipped when the booking is already
        // cancelled, or when nothing was ever created at the provider.
        // ─────────────────────────────────────────────────────────────
        ProviderResultKind? providerKind = null;
        if (!alreadyCancelled && !string.IsNullOrWhiteSpace(providerRef))
        {
            var result = await adapter.CancelBookingAsync(ProviderAccount, providerRef, ct);
            providerKind = result.Kind;

            // Ambiguous outcomes must reconcile before anything else happens (Inv-7): we do not
            // know whether the provider cancelled, so we must not report it or free the seat.
            if (providerKind is not (ProviderResultKind.Created
                or ProviderResultKind.AlreadyExists
                or ProviderResultKind.NotFound))
            {
                return await RouteToReconcilingAsync(tenantId, operationId, providerKind.Value, ct);
            }
        }

        var reason = alreadyCancelled
            ? "already_cancelled"
            : string.IsNullOrWhiteSpace(providerRef)
                ? "cancelled_no_provider_effect"
                : "cancelled";

        return await CommitCancellationAsync(
            tenantId, operationId, booking, cancelStep, providerRef, reason, ct);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>Create-or-load the "cancel" workflow step so a redelivery reuses it (Inv-5).</summary>
    private async Task<WorkflowStep?> CreateOrLoadStepAsync(
        string tenantId, string operationId, CancellationToken ct)
    {
        WorkflowStep? step = null;
        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            var existing = await stepRepo.GetByOperationAndTypeAsync(tenantId, operationId, CancelStepType, ct2);
            if (existing is not null)
            {
                step = existing;
                return;
            }

            var now = DateTimeOffset.UtcNow;
            step = new WorkflowStep
            {
                TenantId = tenantId,
                WorkflowStepId = $"ws_{Guid.NewGuid():N}",
                OperationId = operationId,
                StepType = CancelStepType,
                Status = WorkflowStepStatus.InProgress,
                // The provider key for a cancel is the booking's own reference, which the step
                // does not mint — the original create already owns the idempotent identity.
                ProviderIdempotencyKey = string.Empty,
                AttemptCount = 1,
                Version = 0,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await stepRepo.AddAsync(step, ct2);
        }, ct);

        return step;
    }

    /// <summary>
    /// One transaction: settle the step, the booking, the capacity, the operation, the outbox and
    /// the audit together, so no observer can see a partial cancellation (Inv-4, Inv-8).
    /// </summary>
    private async Task<StepOutcome> CommitCancellationAsync(
        string tenantId,
        string operationId,
        Booking booking,
        WorkflowStep cancelStep,
        string? providerRef,
        string reason,
        CancellationToken ct)
    {
        var committed = false;

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            var now = DateTimeOffset.UtcNow;

            if (cancelStep.Status == WorkflowStepStatus.InProgress)
            {
                await stepRepo.TryTransitionStatusAsync(
                    tenantId, cancelStep.WorkflowStepId, cancelStep.Version,
                    WorkflowStepStatus.InProgress, WorkflowStepStatus.Succeeded, ct2);
            }

            // Re-read inside the transaction and version-guard: a concurrent delivery may have
            // already settled this operation.
            var freshOp = await opRepo.GetAsync(operationId, ct2);
            if (freshOp is null || freshOp.Status != OperationStatus.Finalizing)
                return;

            var rows = await opRepo.TryTransitionStatusAsync(
                tenantId, operationId, freshOp.Version,
                OperationStatus.Finalizing, OperationStatus.Booked,
                reason: reason, ct: ct2);

            // Lost the race — another delivery is settling it. Do no dependent writes.
            if (rows == 0)
                return;

            committed = true;

            // The booking row is what actually carries the cancellation (MB-Inv-6: a transition,
            // never a delete), and it is a HARD PRECONDITION for everything below: releasing the
            // seat and announcing "cancel confirmed" for a booking that is still live would hand
            // someone else a seat the guest still holds, with no operation left to re-drive it.
            // The guard is version-only, so 0 rows means a concurrent writer moved the row —
            // re-read (reads are AsNoTracking, so this sees the committed version) and retry.
            var lifecycleSettled = false;
            var lastVersion = 0L;

            // Did THIS transaction perform the cancellation, or did it find one already done?
            //
            // That is the only honest basis for sending "your cancellation is confirmed", and it is
            // not what `alreadyCancelled` records: that was read BEFORE the provider call, so a
            // second concurrent cancel of the same booking still carried `false` into the commit,
            // found the booking already Cancelled inside the transaction, and emitted a SECOND
            // notification for something the guest had already been told.
            var cancelledHere = false;

            for (var attempt = 0; attempt < BookingTransitionAttempts; attempt++)
            {
                var freshBooking = await bookingRepo.GetByIdAsync(tenantId, booking.BookingId, ct2)
                    ?? throw new PoisonMessageException(
                        $"Booking '{booking.BookingId}' for cancel operation '{operationId}' no longer exists.");

                if (freshBooking.LifecycleStatus == BookingLifecycleStatus.Cancelled)
                {
                    lifecycleSettled = true;
                    break;
                }

                lastVersion = freshBooking.Version;

                var bookingRows = await bookingRepo.TryTransitionLifecycleAsync(
                    tenantId, booking.BookingId, freshBooking.Version,
                    BookingLifecycleStatus.Cancelled, now, ct2);

                if (bookingRows > 0)
                {
                    lifecycleSettled = true;
                    cancelledHere = true;
                    break;
                }
            }

            if (!lifecycleSettled)
            {
                // Roll the whole transaction back — including the operation's move to Booked — so
                // the message is redelivered against a settled row. Provider cancellation is
                // idempotent (AlreadyExists/NotFound are successes), so re-running is safe.
                var current = await bookingRepo.GetByIdAsync(tenantId, booking.BookingId, ct2);
                throw new StaleBookingVersionException(
                    booking.BookingId, lastVersion, current?.Version ?? lastVersion);
            }

            // Give the seat back. The cancellation holds no capacity of its own — the capacity
            // belongs to the operation that created the booking, whose hold is Finalized.
            var origin = await opRepo.GetBookedOperationForBookingAsync(tenantId, booking.BookingId, ct2);
            if (origin is not null)
            {
                await capacityReservation.ReleaseFinalizedByOperationAsync(
                    tenantId, origin.OperationId, ReleaseReason.ProviderReleased, ct2);
            }

            // Inv-8: committed in the same transaction as the state it describes.
            //
            // Only ONE cancellation of a booking is news, and the transition this transaction
            // performed is what proves which call it was. A second cancel operation against an
            // already-cancelled booking still settles (it is a legitimate no-op), but notifying
            // again would send the guest a second "your cancellation is confirmed" message for
            // something they were already told. The audit event below is still written — that is
            // an internal record, and it carries `reason = already_cancelled`.
            if (cancelledHere)
            {
                await outboxRepo.AddAsync(new OutboxMessage
                {
                    TenantId = tenantId,
                    MessageId = $"obx_{Guid.NewGuid():N}",
                    OperationId = operationId,
                    MessageType = CancelConfirmedEventType,
                    CorrelationId = operationId,
                    Payload = JsonSerializer.Serialize(new
                    {
                        operation_id = operationId,
                        tenant_id = tenantId,
                        booking_id = booking.BookingId,
                        reason,
                    }),
                    OccurredAt = now,
                }, ct2);
            }

            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = tenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "operation.cancelled",
                Actor = "system",
                SubjectRef = operationId,
                CorrelationId = operationId,
                OperationId = operationId,
                Metadata = JsonSerializer.Serialize(new
                {
                    booking_id = booking.BookingId,
                    provider_ref = providerRef,
                    reason,
                }),
                OccurredAt = now,
            }, ct2);
        }, ct);

        // Emitted AFTER the transaction returns, not inside it — the same reason the modify lane
        // moved it out. A counter increment is not transactional: if the commit failed after the
        // delegate ran (an outbox or audit unique violation, a dropped connection), the operation
        // would stay Finalizing while the counter had already recorded a terminal `booked`, and the
        // redelivery that follows would count it again. `committed` is only set on the path that
        // actually wrote, so a lost race (rows == 0) contributes nothing either.
        if (committed)
        {
            metrics.FulfillmentOperationTerminalTotal.Add(
                1, new KeyValuePair<string, object?>(MetricNames.Tags.State, "booked"));
        }

        return committed ? StepOutcome.Terminal : StepOutcome.Continue;
    }

    /// <summary>
    /// Inv-7: an uncertain provider outcome parks the operation in <c>Reconciling</c>. The seat is
    /// deliberately NOT given back and the customer is NOT told anything — we do not yet know
    /// whether the booking still exists.
    /// </summary>
    private async Task<StepOutcome> RouteToReconcilingAsync(
        string tenantId, string operationId, ProviderResultKind kind, CancellationToken ct)
    {
        var moved = false;

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            var freshOp = await opRepo.GetAsync(operationId, ct2);
            if (freshOp is null || freshOp.Status != OperationStatus.Finalizing)
                return;

            var rows = await opRepo.TryTransitionStatusAsync(
                tenantId, operationId, freshOp.Version,
                OperationStatus.Finalizing, OperationStatus.Reconciling,
                reason: $"cancel_{kind.ToString().ToLowerInvariant()}", ct: ct2);

            if (rows > 0)
                moved = true;
        }, ct);

        // Lost race → Continue so the driver reloads; otherwise Wait for the reconciler.
        return moved ? StepOutcome.Wait : StepOutcome.Continue;
    }
}
