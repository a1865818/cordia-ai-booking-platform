using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Observability;
using Cordia.Infrastructure.Observability;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.ModifyBooking;

/// <summary>Fulfils a modify/reschedule operation at <c>Finalizing</c>.</summary>
public sealed record ModifyBookingCommand(string TenantId, string OperationId)
    : IRequest<StepOutcome>;

/// <summary>
/// Fulfils a modification or reschedule (spec 06 §11, spec 08 §9.4.8).
///
/// <para>Before this existed the fulfilment engine could only create and cancel. A modify
/// reaching <c>Finalizing</c> was refused with a <c>PoisonMessageException</c> — deliberately,
/// because <c>FinalizeBookingCommandHandler</c> calls <c>CreateBookingAsync</c> unconditionally,
/// so letting one through would have put a SECOND booking into the venue's system while the
/// original stayed pending, and reported <c>booked</c> for it.</para>
///
/// <para><b>One provider call, not cancel-then-create.</b> §9.4.8 requires a modification to
/// preserve the original reservation until a replacement is secured. Cancelling first surrenders
/// the seat, so if the new time turns out to be unavailable the guest is left with nothing —
/// worse than the modification simply failing. <c>ModifyBookingAsync</c> either moves the booking
/// or leaves it exactly as it was.</para>
///
/// Protocol (mirrors <c>CancelBookingCommandHandler</c>, the proven staged-call shape):
///   1. Load the operation; guard tenant, status == Finalizing, type == Modify | Reschedule.
///   2. Load the target booking from <c>op.BookingId</c>.
///   3. Create-or-load the "modify" workflow step (idempotent, Inv-5).
///   4. Call <c>adapter.ModifyBookingAsync</c> <b>outside</b> the transaction (§12.6).
///   5a. Created / AlreadyExists / NotFound — one transaction: step → Succeeded; operation
///       Finalizing → Booked (reason "modified"); outbox <c>cancel_or_modify_confirmed</c>; audit.
///   5b. Ambiguous / Transient / PermanentError / AuthError ⇒ Finalizing → Reconciling (Inv-7).
///       Never a blind retry — we do not know whether the provider applied the amendment.
///
/// <para><b>The booking's lifecycle does NOT change.</b> A modified booking is still confirmed;
/// only its details moved, and those live on the new intent revision, which the read projection
/// already joins. Flipping the lifecycle would tell every reader the booking had been cancelled
/// and recreated, which is precisely what did not happen.</para>
///
/// <para><b>Inv-8:</b> the operation transition and the notification outbox row commit in the
/// same transaction, so a guest can never be told "moved" before it is true.</para>
/// </summary>
public sealed class ModifyBookingCommandHandler(
    IBookingOperationRepository opRepo,
    IBookingRepository bookingRepo,
    IBookingIntentRepository intentRepo,
    IWorkflowStepRepository stepRepo,
    IBookingProviderAdapter adapter,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    IUnitOfWork uow,
    ICapacityReservation capacityReservation,
    Metrics metrics)
    : IRequestHandler<ModifyBookingCommand, StepOutcome>
{
    private const string ModifyStepType = "modify";
    private const string ProviderAccount = "default";

    /// <summary>
    /// Shared with cancel by design: the ratified event is <c>cancel_or_modify_confirmed</c>, and
    /// its template covers both outcomes. Minting a second message type would need a registry
    /// entry that does not exist, and an unregistered type dead-letters.
    /// </summary>
    public const string ModifyConfirmedEventType = "notification.cancel_confirmed.v1";

    public async Task<StepOutcome> Handle(ModifyBookingCommand command, CancellationToken ct)
    {
        var op = await opRepo.GetAsync(command.OperationId, ct);

        if (op is null || op.TenantId != command.TenantId)
            return StepOutcome.Wait;

        if (op.Status != OperationStatus.Finalizing)
            return StepOutcome.Continue;

        if (op.OperationType is not (Domain.Generated.OperationTypeEnum.Modify
            or Domain.Generated.OperationTypeEnum.Reschedule))
        {
            return StepOutcome.Continue;
        }

        var tenantId = command.TenantId;
        var operationId = command.OperationId;

        var revision = await intentRepo.GetRevisionAsync(tenantId, op.IntentId, op.IntentRevision, ct)
            ?? throw new PoisonMessageException(
                $"Modify operation '{operationId}' has no revision {op.IntentRevision} to apply.");

        // The operation row is the primary source, but acceptance only began carrying the target
        // recently — fall back to the canonical payload, which is where a mutation's target has
        // always lived, so operations accepted before that still fulfil.
        var targetBookingId = string.IsNullOrWhiteSpace(op.BookingId)
            ? ReadTargetBookingId(revision.CanonicalPayload)
            : op.BookingId;

        // A modification with no target is a defect, not a transient failure: retrying can never
        // discover one. Poison it so it is quarantined and surfaced (Inv-14) rather than looping.
        if (string.IsNullOrWhiteSpace(targetBookingId))
            throw new PoisonMessageException(
                $"Modify operation '{operationId}' carries no target booking id.");

        var booking = await bookingRepo.GetByIdAsync(tenantId, targetBookingId, ct)
            ?? throw new PoisonMessageException(
                $"Modify operation '{operationId}' targets booking '{targetBookingId}', which does not exist.");

        // A cancelled booking cannot be moved. This is a business outcome, not a failure to
        // retry: the guest must make a new booking.
        if (booking.LifecycleStatus == BookingLifecycleStatus.Cancelled)
        {
            return await RouteToTerminalAsync(
                tenantId, operationId, OperationStatus.Rejected, "target_booking_cancelled", ct);
        }

        var step = await CreateOrLoadStepAsync(tenantId, operationId, ct);
        if (step is null)
            return StepOutcome.Continue;

        // ─────────────────────────────────────────────────────────────
        // External call — OUTSIDE the transaction (§12.6).
        // ─────────────────────────────────────────────────────────────
        var providerRef = booking.ProviderReference;
        var reason = "modified";

        if (!string.IsNullOrWhiteSpace(providerRef))
        {
            var result = await adapter.ModifyBookingAsync(
                ProviderAccount,
                providerRef,
                new ProviderBookingRequest(
                    tenantId,
                    operationId,
                    ProviderAccount,
                    // The operation id IS the stable key: each modification is its own operation,
                    // and a redelivery must present the same key so the provider can dedupe.
                    operationId,
                    revision.PayloadHash,
                    revision.CanonicalPayload),
                ct);

            // Ambiguous outcomes must reconcile before anything else happens (Inv-7): we do not
            // know whether the provider applied the amendment, so we must not report that it did.
            if (result.Kind is not (ProviderResultKind.Created
                or ProviderResultKind.AlreadyExists
                or ProviderResultKind.NotFound))
            {
                return await RouteToReconcilingAsync(tenantId, operationId, result.Kind, ct);
            }

            // NotFound: the provider has no such booking, so there was nothing to move. The
            // operation still settles — from current state, without claiming a provider effect.
            if (result.Kind == ProviderResultKind.NotFound)
                reason = "modified_no_provider_effect";
        }
        else
        {
            // Never reached a provider in the first place (first-party only).
            reason = "modified_no_provider_effect";
        }

        return await CommitModificationAsync(tenantId, operationId, booking, step, reason, ct);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// The mutation target from the canonical payload — where it has always lived, and the only
    /// source for operations accepted before the operation row began carrying it.
    /// </summary>
    private static string? ReadTargetBookingId(string? canonicalPayload)
    {
        if (string.IsNullOrWhiteSpace(canonicalPayload))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(canonicalPayload);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("target_booking_id", out var v)
                   && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Create-or-load the "modify" workflow step so a redelivery reuses it (Inv-5).</summary>
    private async Task<WorkflowStep?> CreateOrLoadStepAsync(
        string tenantId, string operationId, CancellationToken ct)
    {
        WorkflowStep? step = null;
        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            var existing = await stepRepo.GetByOperationAndTypeAsync(tenantId, operationId, ModifyStepType, ct2);
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
                StepType = ModifyStepType,
                Status = WorkflowStepStatus.InProgress,
                ProviderIdempotencyKey = operationId,
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
    /// One transaction: settle the step, the operation, the outbox and the audit together, so no
    /// observer can see a partial modification (Inv-4, Inv-8).
    /// </summary>
    private async Task<StepOutcome> CommitModificationAsync(
        string tenantId,
        string operationId,
        Booking booking,
        WorkflowStep step,
        string reason,
        CancellationToken ct)
    {
        var committed = false;

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            var now = DateTimeOffset.UtcNow;

            if (step.Status == WorkflowStepStatus.InProgress)
            {
                await stepRepo.TryTransitionStatusAsync(
                    tenantId, step.WorkflowStepId, step.Version,
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

            // --- Capacity ownership transfers to THIS operation (spec 07 §6.1/§6.2). ---
            //
            // A reschedule reserves capacity like any other operation: ValidateOperationCommandHandler
            // routes every non-Cancel type through ReservingCapacity, so this operation already
            // holds an Active hold on the NEW slot. But `FinalizeAgainstOperationAsync` — the only
            // Active→Finalized transition — is called exclusively from FinalizeBookingCommandHandler,
            // which the modify lane deliberately bypasses. Nothing settled it, and nothing released
            // the ORIGINAL slot either, so a reschedule used to leave the ledger wrong in both
            // directions at once:
            //
            //   * the old slot stayed booked forever, for a booking that had moved away from it;
            //   * the new slot's hold sat Active until the expiry sweeper released it, after which
            //     the slot read FREE while a confirmed booking occupied it — so the venue could be
            //     handed a second booking for the same seat.
            //
            // It also broke cancellation: `GetBookedOperationForBookingAsync` returns the NEWEST
            // Booked op for the booking, which after a reschedule is this one — and releasing
            // against an operation with no Finalized hold silently released nothing, so cancelling
            // a rescheduled booking never gave the seat back.
            //
            // Ordered old-then-new and both inside this transaction: the release is what makes the
            // seat we are giving up available again, and if either write fails the whole settlement
            // rolls back with the operation, rather than leaving capacity half-moved.
            var previousOwner = await opRepo.GetBookedOperationForBookingAsync(
                tenantId, booking.BookingId, ct2);

            if (previousOwner is not null && previousOwner.OperationId != operationId)
            {
                // `Superseded`, not `ProviderReleased`: the seat is not being given back to the
                // venue, it is being handed from the old slot to the new one by this very
                // operation. Cancel uses ProviderReleased because there the booking really is gone.
                await capacityReservation.ReleaseFinalizedByOperationAsync(
                    tenantId, previousOwner.OperationId, ReleaseReason.Superseded, ct2);
            }

            // NotTracked is the normal answer for a venue that has declared no slots — capacity is
            // unenforced there by decision, not by accident, so it is not an error.
            await capacityReservation.FinalizeAgainstOperationAsync(tenantId, operationId, "create", ct2);

            // A modification accepted through `POST /api/Bookings/{id}/modifications` parks the
            // booking at ModificationPending so concurrent writers see the change in flight. That
            // front door is the ONLY writer of that status and nothing cleared it, so before this
            // the booking stayed "modification pending" forever — the operation reported `booked`
            // while every reader of the booking row was told a change was still outstanding.
            // Cancel has always closed its own pending state (CancellationPending → Cancelled);
            // this is the same discipline for the modify lane.
            //
            // Re-read inside the transaction: the version moved if the amendment raced anything
            // else, and a stale token would silently no-op. Only ModificationPending is cleared —
            // a booking Cancelled underneath us must stay cancelled.
            var freshBooking = await bookingRepo.GetByIdAsync(tenantId, booking.BookingId, ct2);
            if (freshBooking is { LifecycleStatus: BookingLifecycleStatus.ModificationPending })
            {
                // Losing this race is benign and needs no branch: the only other writer of this
                // row is settling the same booking, and the operation transition above already
                // carried the authoritative outcome.
                await bookingRepo.TryTransitionLifecycleAsync(
                    tenantId, freshBooking.BookingId, freshBooking.Version,
                    BookingLifecycleStatus.Confirmed, now, ct2);
            }

            // Otherwise the booking's lifecycle is deliberately untouched. A modified booking is
            // still confirmed; what moved lives on the new intent revision, which the read
            // projection already joins to the operation.
            await outboxRepo.AddAsync(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = $"obx_{Guid.NewGuid():N}",
                OperationId = operationId,
                MessageType = ModifyConfirmedEventType,
                CorrelationId = operationId,
                Payload = JsonSerializer.Serialize(new
                {
                    operation_id = operationId,
                    tenant_id = tenantId,
                    booking_id = booking.BookingId,
                }),
                OccurredAt = now,
            }, ct2);

            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = tenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "booking.modified",
                Actor = "system",
                SubjectRef = booking.BookingId,
                CorrelationId = operationId,
                OperationId = operationId,
                Metadata = JsonSerializer.Serialize(new { reason }),
                OccurredAt = now,
            }, ct2);

            committed = true;
        }, ct);

        // Emitted AFTER the transaction returns, not inside it. A counter increment is not
        // transactional: if the commit failed after the delegate ran — an outbox or audit unique
        // violation, a dropped connection — the operation would stay Finalizing while the counter
        // had already recorded a terminal `booked`, and the redelivery that follows would count it
        // a second time. Keyed on `committed`, which is only set on the path that actually wrote,
        // so a lost race (rows == 0) contributes nothing either.
        if (committed)
        {
            metrics.FulfillmentOperationTerminalTotal.Add(
                1, new KeyValuePair<string, object?>(MetricNames.Tags.State, "booked"));
        }

        return committed ? StepOutcome.Terminal : StepOutcome.Continue;
    }

    /// <summary>
    /// Ambiguity is a first-class state (Inv-7): the reconciler establishes what the provider
    /// actually did before anything is reported or retried.
    /// </summary>
    private async Task<StepOutcome> RouteToReconcilingAsync(
        string tenantId, string operationId, ProviderResultKind kind, CancellationToken ct)
    {
        return await RouteToTerminalAsync(
            tenantId, operationId, OperationStatus.Reconciling, $"modify_{kind}".ToLowerInvariant(), ct);
    }

    private async Task<StepOutcome> RouteToTerminalAsync(
        string tenantId, string operationId, OperationStatus next, string reason, CancellationToken ct)
    {
        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            var fresh = await opRepo.GetAsync(operationId, ct2);
            if (fresh is null || fresh.Status != OperationStatus.Finalizing)
                return;

            await opRepo.TryTransitionStatusAsync(
                tenantId, operationId, fresh.Version,
                OperationStatus.Finalizing, next, reason: reason, ct: ct2);
        }, ct);

        return StepOutcome.Continue;
    }
}
