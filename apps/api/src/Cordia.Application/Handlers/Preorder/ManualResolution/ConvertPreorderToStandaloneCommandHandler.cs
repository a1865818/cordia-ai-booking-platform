using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Preorder;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Preorder.ManualResolution;

/// <summary>
/// Converts a pre-order parked in <c>ManualReview</c> into a standalone order (spec 11 §21.4). This is
/// the audited operator + customer resolution of the "booking committed but pre-order could not be
/// bound" park (<c>CompensatingBooking → ManualReview</c>): rather than leaving the pre-order to
/// linger, an operator — with recorded customer agreement — finalizes it as a standalone food order.
///
/// <para>Advances <c>ManualReview → Completed</c> under the version+state guard, marking the saga
/// <c>IsStandalone</c>, and emits the single combined outcome + an audit event carrying BOTH the
/// operator actor and the customer-agreement evidence id (§21.4). Idempotent: an already-Completed
/// saga returns success; any other non-ManualReview state is a 409.</para>
/// </summary>
public sealed class ConvertPreorderToStandaloneCommandHandler(
    IPreorderSagaRepository sagaRepo,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
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

        // Idempotent: already resolved by a prior convert (standalone Completed).
        if (saga.SagaState == PreorderSagaState.Completed)
        {
            logger.LogInformation("Preorder convert: saga {SagaId} already Completed; settling.", command.SagaId);
            return Unit.Value;
        }

        // Only a ManualReview park is convertible (§21.4). Reject any other state as a 409.
        if (saga.SagaState != PreorderSagaState.ManualReview)
            throw new InvalidPreorderSagaTransitionException(saga.SagaState, PreorderSagaState.Completed);

        PreorderSagaStateMachine.EnsureAllowed(PreorderSagaState.ManualReview, PreorderSagaState.Completed);

        var won = false;
        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            won = await sagaRepo.TryAdvanceStateAsync(
                command.TenantId, command.SagaId, saga.Version,
                PreorderSagaState.ManualReview, PreorderSagaState.Completed, now,
                setFailureReason: "converted_to_standalone", setStandalone: true, ct: ct2) > 0;
            if (!won)
                return;

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

            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = command.TenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "preorder.standalone_converted",
                Actor = command.OperatorActor,
                SubjectRef = command.SagaId,
                CorrelationId = command.SagaId,
                OperationId = saga.OperationId,
                Metadata = JsonSerializer.Serialize(new
                {
                    saga_id = command.SagaId,
                    operation_id = saga.OperationId,
                    // §21.4 audit: BOTH parties on record — the operator who acted and the customer's agreement.
                    operator_actor = command.OperatorActor,
                    customer_agreement_evidence_id = command.CustomerAgreementEvidenceId,
                }),
                OccurredAt = now,
            }, ct2);
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
