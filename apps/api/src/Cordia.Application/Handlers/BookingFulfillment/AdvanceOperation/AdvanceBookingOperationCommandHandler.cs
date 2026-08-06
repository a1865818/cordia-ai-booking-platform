using System.Text.Json;
using Cordia.Application.Handlers.BookingFulfillment.CancelBooking;
using Cordia.Application.Handlers.BookingFulfillment.ModifyBooking;
using Cordia.Application.Handlers.BookingFulfillment.Compensate;
using Cordia.Application.Handlers.BookingFulfillment.Finalize;
using Cordia.Application.Handlers.BookingFulfillment.Reconcile;
using Cordia.Application.Handlers.BookingFulfillment.RequestPayment;
using Cordia.Application.Handlers.BookingFulfillment.ReserveCapacity;
using Cordia.Application.Handlers.BookingFulfillment.Validate;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Messaging;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.AdvanceOperation;

/// <summary>
/// Drives a booking operation through successive workflow steps in a tight loop (spec 06 §6.2).
///
/// Loop invariant: reload → pick step → dispatch → if Continue, repeat; else break.
/// Max 20 iterations guard against runaway loops caused by unexpected state.
///
/// Inbox admission is best-effort (INSERT … ON CONFLICT DO NOTHING inside its own short
/// transaction) and does NOT gate execution — version-guarded transitions + the sweeper are the
/// real idempotency/recovery mechanism (§12.6).
///
/// <para><strong>T11 Poison/DLQ (Inv-14):</strong> if a step handler throws
/// <see cref="PoisonMessageException"/> or <see cref="InvalidStateTransitionException"/> (an
/// invariant-violation defect that will never self-heal), the driver:
///   1. Creates an <c>operations_work_items</c> row (if none open) + emits
///      <c>notification.still_confirming.v1</c> + writes an audit event — all in a committed txn.
///   2. AFTER that txn commits, dead-letters the transport message via
///      <see cref="IDeadLetterHandler.QuarantineAsync"/>.
///   3. Returns <see cref="Unit.Value"/> (ACK) so the message is not redelivered.
/// The operation stays in its current recoverable in-progress status (DLQ ≠ business state).</para>
/// </summary>
public sealed class AdvanceBookingOperationCommandHandler(
    IMediator mediator,
    IBookingOperationRepository opRepo,
    IInboxRepository inboxRepo,
    IUnitOfWork uow,
    IDeadLetterHandler deadLetterHandler,
    IOperationWorkItemRepository workItemRepo,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo)
    : IRequestHandler<AdvanceBookingOperationCommand, Unit>
{
    private const int MaxIterations = 20;

    public async Task<Unit> Handle(AdvanceBookingOperationCommand command, CancellationToken ct)
    {
        // Best-effort inbox record for audit/dedup visibility (§12.6).
        // Run in its own short transaction and ignore the result — idempotency comes from
        // version-guarded transitions, not from inbox suppression.
        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            await inboxRepo.TryAdmitAsync(new ConsumerInbox
            {
                TenantId = command.TenantId,
                ConsumerName = "booking.driver",
                MessageId = command.MessageId,
                CorrelationId = command.OperationId,
                OperationId = command.OperationId,
                Outcome = InboxOutcome.Admitted,
                AdmittedAt = DateTimeOffset.UtcNow,
            }, ct2);
        }, ct);

        try
        {
            // Drive loop: advance the operation step by step until terminal or waiting.
            for (var i = 0; i < MaxIterations; i++)
            {
                var op = await opRepo.GetAsync(command.OperationId, ct);
                if (op is null || op.TenantId != command.TenantId)
                    return Unit.Value;

                if (OperationStateMachine.IsTerminal(op.Status))
                    return Unit.Value;

                IBaseRequest? stepCommand = op.Status switch
                {
                    OperationStatus.Accepted or OperationStatus.Validating
                        => new ValidateOperationCommand(command.TenantId, command.OperationId),
                    OperationStatus.ReservingCapacity
                        => new ReserveCapacityCommand(command.TenantId, command.OperationId),
                    // T7: Held routes to RequestPayment (which either goes Held→Finalizing no-deposit,
                    // or Held→AwaitingPayment deposit path). Finalizing routes directly to Finalize.
                    OperationStatus.Held
                        => new RequestPaymentCommand(command.TenantId, command.OperationId),
                    // Spec 08 §5.3: at Finalizing the external call differs by operation type —
                    // a create books with the provider, a cancel un-books. This is the one place
                    // the driver is type-aware; everything else stays status-driven.
                    OperationStatus.Finalizing when op.OperationType == Domain.Generated.OperationTypeEnum.Cancel
                        => new CancelBookingCommand(command.TenantId, command.OperationId),
                    // Modify/reschedule has its own fulfilment step: FinalizeBookingCommandHandler
                    // calls CreateBookingAsync unconditionally, so routing one there would put a
                    // SECOND, disconnected booking into the venue's system while the original
                    // stayed pending — and report `booked` for it. ModifyBookingCommandHandler
                    // amends the existing booking in one provider call instead, preserving the
                    // original reservation (§9.4.8).
                    OperationStatus.Finalizing when op.OperationType
                        is Domain.Generated.OperationTypeEnum.Modify
                        or Domain.Generated.OperationTypeEnum.Reschedule
                        => new ModifyBookingCommand(command.TenantId, command.OperationId),
                    OperationStatus.Finalizing
                        => new FinalizeBookingCommand(command.TenantId, command.OperationId),
                    OperationStatus.Reconciling
                        => new ReconcileOperationCommand(command.TenantId, command.OperationId),
                    OperationStatus.CompensationRequired
                        => new CompensateOperationCommand(command.TenantId, command.OperationId),
                    // States that require external input or are handled by later tickets: stop and wait.
                    // ManualReview is handled by T10; AwaitingPayment/AwaitingUserAction/AlternativesRequired etc.
                    _ => null,
                };

                if (stepCommand is null)
                    break; // AwaitingUserAction, AwaitingPayment, AlternativesRequired, etc. — wait states

                var outcome = await mediator.Send((IRequest<StepOutcome>)stepCommand, ct);
                if (outcome != StepOutcome.Continue)
                    break;
            }
        }
        catch (Exception ex) when (ex is PoisonMessageException or InvalidStateTransitionException)
        {
            // Inv-14 / FM#14: invariant-violation defect that will never self-heal.
            // Persist business side-effects in a committed txn BEFORE dead-lettering.
            await PersistPoisonSideEffectsAsync(command, ex, ct);

            // AFTER the txn commits: dead-letter the transport message (transport-level step).
            var envelope = BuildEnvelope(command);
            var errorClass = ex is PoisonMessageException ? "poison" : "invalid_state_transition";
            await deadLetterHandler.QuarantineAsync(
                envelope,
                consumerName: "booking.driver",
                errorClass: errorClass,
                attemptHistoryJson: "[]",
                firstFailedAt: DateTimeOffset.UtcNow,
                ct: ct);

            // ACK — message is durably dead-lettered, not redelivered.
            return Unit.Value;
        }

        return Unit.Value;
    }

    /// <summary>
    /// Persists the poison work-item, still-confirming notification, and audit event atomically
    /// (Inv-14: business state committed BEFORE dead-lettering the transport message).
    ///
    /// The operation stays in its current recoverable in-progress status — DLQ ≠ business state.
    /// </summary>
    private async Task PersistPoisonSideEffectsAsync(
        AdvanceBookingOperationCommand command,
        Exception ex,
        CancellationToken ct)
    {
        var errorClass = ex is PoisonMessageException ? "poison" : "invalid_state_transition";
        var tenantId = command.TenantId;
        var operationId = command.OperationId;
        var deterministicOutboxId = $"obx_poison_{operationId}";

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            // Create work item only if none open (idempotent).
            var existing = await workItemRepo.GetOpenByOperationAsync(tenantId, operationId, ct2);
            if (existing is null)
            {
                var now = DateTimeOffset.UtcNow;
                await workItemRepo.AddAsync(new OperationWorkItem
                {
                    TenantId = tenantId,
                    WorkItemId = $"wi_{Guid.NewGuid():N}",
                    OperationId = operationId,
                    Reason = $"poison:{errorClass}",
                    State = "open",
                    SlaDeadline = now.AddHours(1),
                    CreatedAt = now,
                    Version = 0,
                }, ct2);
            }

            // Emit notification.still_confirming.v1 ONCE (deterministic message_id dedup).
            var outboxExists = await outboxRepo.ExistsAsync(tenantId, deterministicOutboxId, ct2);
            if (!outboxExists)
            {
                await outboxRepo.AddAsync(new OutboxMessage
                {
                    TenantId = tenantId,
                    MessageId = deterministicOutboxId,
                    OperationId = operationId,
                    MessageType = "notification.still_confirming.v1",
                    CorrelationId = operationId,
                    Payload = JsonSerializer.Serialize(new
                    {
                        operation_id = operationId,
                        tenant_id = tenantId,
                    }),
                    OccurredAt = DateTimeOffset.UtcNow,
                }, ct2);
            }

            // Audit event.
            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = tenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "operation.poison_quarantined",
                Actor = "system",
                SubjectRef = operationId,
                CorrelationId = operationId,
                OperationId = operationId,
                Metadata = JsonSerializer.Serialize(new
                {
                    error_class = errorClass,
                    message_id = command.MessageId,
                    message_type = command.MessageType,
                }),
                OccurredAt = DateTimeOffset.UtcNow,
            }, ct2);
        }, ct);
    }

    /// <summary>
    /// Reconstructs a <see cref="JobEnvelope"/> from the driver command fields so that
    /// <see cref="IDeadLetterHandler.QuarantineAsync"/> receives a well-formed envelope
    /// without the driver needing to hold a reference to the original transport envelope.
    /// </summary>
    private static JobEnvelope BuildEnvelope(AdvanceBookingOperationCommand command)
    {
        var payload = command.Payload.ValueKind == JsonValueKind.Undefined
            ? JsonDocument.Parse("{}").RootElement.Clone()
            : command.Payload;

        return new JobEnvelope(
            MessageId: command.MessageId,
            MessageType: string.IsNullOrEmpty(command.MessageType)
                ? "booking.command.requested.v1"
                : command.MessageType,
            OccurredAt: DateTimeOffset.UtcNow,
            TenantId: command.TenantId,
            CorrelationId: command.OperationId,
            CausationId: null,
            OperationId: command.OperationId,
            IntentId: null,
            IntentRevision: null,
            AttemptHint: 0,
            Payload: payload);
    }
}
