using Cordia.Application.Common.Audit;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Preorder;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Preorder.ManualResolution;

/// <summary>
/// Cancels a pre-order parked in <c>ManualReview</c> (spec 11 §21.4 — the "or cancel" resolution).
/// Advances <c>ManualReview → FailedFinal</c> under the version+state guard, emits the single combined
/// failure outcome (Inv-8), and audits the operator action. Idempotent: an already-FailedFinal saga
/// returns success; any other non-ManualReview state is a 409.
/// </summary>
public sealed class CancelPreorderManualReviewCommandHandler(
    IPreorderSagaRepository sagaRepo,
    IPreorderItemReservation items,
    IOutboxRepository outboxRepo,
    IAuditWriter audit,
    IUnitOfWork uow,
    ILogger<CancelPreorderManualReviewCommandHandler> logger)
    : IRequestHandler<CancelPreorderManualReviewCommand, Unit>
{
    public async Task<Unit> Handle(CancelPreorderManualReviewCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var saga = await sagaRepo.GetAsync(command.SagaId, ct);
        if (saga is null || saga.TenantId != command.TenantId)
            throw new PreorderSagaNotFoundException(command.SagaId);

        // Idempotent: already cancelled.
        if (saga.SagaState == PreorderSagaState.FailedFinal)
        {
            logger.LogInformation("Preorder cancel: saga {SagaId} already FailedFinal; settling.", command.SagaId);
            return Unit.Value;
        }

        if (saga.SagaState != PreorderSagaState.ManualReview)
            throw new InvalidPreorderSagaTransitionException(saga.SagaState, PreorderSagaState.FailedFinal);

        PreorderSagaStateMachine.EnsureAllowed(PreorderSagaState.ManualReview, PreorderSagaState.FailedFinal);

        // Inv-12: FailureReason (and the outcome payload) is a MACHINE code, never free-form operator
        // text — a human might type customer/card data into command.Reason. The operator's note is kept
        // only in the audit metadata (the human-readable, access-controlled trail).
        const string failureCode = "manual_review_cancelled";
        var operatorNote = string.IsNullOrWhiteSpace(command.Reason) ? null : command.Reason;

        var won = false;
        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            won = await sagaRepo.TryAdvanceStateAsync(
                command.TenantId, command.SagaId, saga.Version,
                PreorderSagaState.ManualReview, PreorderSagaState.FailedFinal, now,
                setFailureReason: failureCode, ct: ct2) > 0;
            if (!won)
                return;

            // §5.3 "FailedFinal: holds/items released": ManualReview is reached from
            // CommittingPreorder, AFTER the items were reserved — cancelling must release them or
            // the reservation leaks against the fulfillment inventory. Idempotent by (tenant, saga).
            await items.ReleaseAsync(command.TenantId, command.SagaId, ct2);

            // ManualReview is reached only AFTER 06 committed the booking — the table reservation
            // stays confirmed. Emit the dedicated "pre-order cancelled, booking stands" outcome,
            // never the FinalFailure booking template.
            await outboxRepo.AddAsync(PreorderSagaSupport.PreorderCancelledOutcome(
                command.TenantId, command.SagaId, saga.OperationId, failureCode, now), ct2);
            // Attributed to the operator (not "system"); the free-form note lives here only.
            // Spec 14 §24.5 — operator action: reason-coded envelope via the audit writer.
            await audit.WriteAsync(new AuditRecord(
                TenantId: command.TenantId,
                ActorType: command.ActorRole, // GD-1: owner may perform operator actions; audit the ASSERTED role
                ActorId: command.OperatorActor,
                Action: "preorder.manual_review_cancelled",
                TargetType: "preorder_saga",
                TargetId: command.SagaId,
                Decision: "allow",
                DataClasses: ["booking"],
                ReasonCode: command.ReasonCode,
                CorrelationId: command.SagaId,
                OperationId: saga.OperationId,
                OccurredAt: now,
                Extra: new { saga_id = command.SagaId, operator_actor = command.OperatorActor, reason = failureCode, operator_note = operatorNote }), ct2);
        }, ct);

        if (!won)
        {
            var reloaded = await sagaRepo.GetAsync(command.SagaId, ct);
            if (reloaded?.SagaState == PreorderSagaState.FailedFinal)
                return Unit.Value; // concurrent cancel won — idempotent
            throw new InvalidPreorderSagaTransitionException(
                reloaded?.SagaState ?? saga.SagaState, PreorderSagaState.FailedFinal);
        }

        return Unit.Value;
    }
}
