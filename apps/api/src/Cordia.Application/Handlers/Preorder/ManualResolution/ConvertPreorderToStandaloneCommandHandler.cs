using System.Text.Json;
using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Preorder;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Preorder.ManualResolution;

/// <summary>
/// Converts a pre-order parked in <c>ManualReview</c> into a standalone order (spec 11 §21.4). This is
/// the audited operator + customer resolution of the "booking committed but pre-order could not be
/// bound" park (<c>CompensatingBooking → ManualReview</c>): rather than leaving the pre-order to
/// linger, an operator — with the customer's recorded agreement — finalizes it as a standalone order.
///
/// <para>The customer half of §21.4 is enforced as a real, single-use spec-08 verification grant: the
/// handler resolves the customer + committed booking from the saga's operation and
/// <see cref="IGrantAuthorizationService.ConsumeGrantAsync"/>s the supplied grant (<c>Modify</c>
/// action, scoped to that booking) inside the same transaction — a missing/expired/mismatched grant
/// throws <c>VerificationRequiredException</c> (403) and rolls the whole conversion back. The operator
/// half is the audited actor; staff RBAC over which operators may act is the §12 open decision.</para>
///
/// <para>Advances <c>ManualReview → Completed</c> under the version+state guard, marking the saga
/// <c>IsStandalone</c>, and emits the single combined outcome + an audit event carrying BOTH the
/// operator actor and the consumed customer grant id (§21.4). Idempotent: an already-Completed saga
/// returns success WITHOUT consuming a grant; any other non-ManualReview state is a 409.</para>
/// </summary>
public sealed class ConvertPreorderToStandaloneCommandHandler(
    IPreorderSagaRepository sagaRepo,
    IBookingReadRepository bookingRead,
    IBookingRepository bookings,
    IGrantAuthorizationService grants,
    IOutboxRepository outboxRepo,
    IAuditWriter audit,
    IUnitOfWork uow,
    ILogger<ConvertPreorderToStandaloneCommandHandler> logger)
    : IRequestHandler<ConvertPreorderToStandaloneCommand, Unit>
{
    public async Task<Unit> Handle(ConvertPreorderToStandaloneCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var saga = await sagaRepo.GetAsync(command.SagaId, ct);
        if (saga is null || saga.TenantId != command.TenantId)
            throw new PreorderSagaNotFoundException(command.SagaId);

        // Idempotent ONLY for a prior convert (Completed AND IsStandalone) — no fresh grant needed. A
        // normally-completed pre-order (Completed but not standalone) is NOT a convert target: fall
        // through so the state guard below rejects it as a 409 rather than silently "succeeding".
        if (saga.SagaState == PreorderSagaState.Completed && saga.IsStandalone)
        {
            logger.LogInformation("Preorder convert: saga {SagaId} already converted to standalone; settling.", command.SagaId);
            return Unit.Value;
        }

        // Only a ManualReview park is convertible (§21.4). Reject any other state as a 409.
        if (saga.SagaState != PreorderSagaState.ManualReview)
            throw new InvalidPreorderSagaTransitionException(saga.SagaState, PreorderSagaState.Completed);

        PreorderSagaStateMachine.EnsureAllowed(PreorderSagaState.ManualReview, PreorderSagaState.Completed);

        // Resolve the customer + committed booking this ManualReview park sits on (booking_committed →
        // CommittingPreorder → CompensatingBooking → ManualReview), so the customer-agreement grant is
        // scoped to the authoritative booking rather than to operator-supplied input (§21.4, spec 08).
        var projection = await bookingRead.GetBookingProjectionByOperationIdAsync(command.TenantId, saga.OperationId, ct);
        var booking = projection is null
            ? null
            : await bookings.GetByIdAsync(command.TenantId, projection.BookingId, ct);
        if (booking is null)
        {
            // No committed booking to establish customer agreement against — cannot authorize (§21.4).
            // Self-audit the denial (spec 14 §24.5): this VerificationRequiredException is thrown
            // DIRECTLY — not via the self-auditing GrantAuthorizationService — so the operator-action
            // behavior's catch (narrowed to AuthorizationFailedException) does not record it; the
            // thrower owns its deny trail, matching every other direct denial site. No transaction is
            // open yet, so this write persists immediately; CancellationToken.None keeps the record
            // durable even if the client disconnects (B2).
            await audit.WriteAsync(new AuditRecord(
                TenantId: command.TenantId,
                ActorType: command.ActorRole,
                ActorId: command.OperatorActor,
                Action: "preorder.standalone_converted",
                TargetType: "preorder_saga",
                TargetId: command.SagaId,
                Decision: "deny",
                DataClasses: ["booking"],
                ReasonCode: command.ReasonCode,
                CorrelationId: command.SagaId,
                OperationId: saga.OperationId,
                Extra: new { error = "no_committed_booking" }), CancellationToken.None);
            throw new VerificationRequiredException(
                command.TenantId, customerRef: string.Empty, VerificationActionEnum.Modify, targetBookingId: null);
        }

        var won = false;
        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            won = await sagaRepo.TryAdvanceStateAsync(
                command.TenantId, command.SagaId, saga.Version,
                PreorderSagaState.ManualReview, PreorderSagaState.Completed, now,
                setFailureReason: "converted_to_standalone", setStandalone: true, ct: ct2) > 0;
            if (!won)
                return;

            // Customer half of §21.4: consume the single-use verification grant scoped to the committed
            // booking. Throws VerificationRequiredException (403) and rolls back the advance if the grant
            // is missing/expired/for a different customer, action, or booking.
            await grants.ConsumeGrantAsync(
                command.TenantId, command.GrantId, booking.CustomerId,
                VerificationActionEnum.Modify, booking.BookingId, targetType: "booking", ct: ct2);

            await outboxRepo.AddAsync(new OutboxMessage
            {
                TenantId = command.TenantId,
                MessageId = $"obx_{Guid.NewGuid():N}",
                MessageType = PreorderSagaMessages.BookingConfirmedNotification,
                CorrelationId = saga.OperationId,
                OperationId = saga.OperationId,
                Payload = JsonSerializer.Serialize(new
                {
                    saga_id = command.SagaId,
                    operation_id = saga.OperationId,
                    preorder = true,
                    standalone = true,
                }),
                OccurredAt = now,
            }, ct2);

            // Spec 14 §24.5 — operator action: reason-coded envelope via the audit writer.
            await audit.WriteAsync(new AuditRecord(
                TenantId: command.TenantId,
                ActorType: command.ActorRole, // GD-1: owner may perform operator actions; audit the ASSERTED role
                ActorId: command.OperatorActor,
                Action: "preorder.standalone_converted",
                TargetType: "preorder_saga",
                TargetId: command.SagaId,
                Decision: "allow",
                DataClasses: ["booking"],
                ReasonCode: command.ReasonCode,
                CorrelationId: command.SagaId,
                OperationId: saga.OperationId,
                OccurredAt: now,
                Extra: new
                {
                    saga_id = command.SagaId,
                    operation_id = saga.OperationId,
                    // §21.4 audit: BOTH parties on record — the operator who acted and the customer's
                    // agreement, evidenced by the consumed spec-08 verification grant + its booking scope.
                    operator_actor = command.OperatorActor,
                    customer_grant_id = command.GrantId,
                    customer_ref = booking.CustomerId,
                    booking_id = booking.BookingId,
                }), ct2);
        }, ct);

        if (!won)
        {
            // Lost the guarded advance to a concurrent resolution — reconcile against the winner.
            var reloaded = await sagaRepo.GetAsync(command.SagaId, ct);
            if (reloaded?.SagaState == PreorderSagaState.Completed)
                return Unit.Value; // concurrent convert won — idempotent
            throw new InvalidPreorderSagaTransitionException(
                reloaded?.SagaState ?? saga.SagaState, PreorderSagaState.Completed);
        }

        return Unit.Value;
    }
}
