using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.Validate;

/// <summary>
/// Handles validation of a booking operation (spec 06 §6.2, validate step).
///
/// Happy path: Accepted → Validating → ReservingCapacity (all internal, no external call).
/// Both transitions are executed inside a single UoW transaction so they commit atomically.
/// A fresh GetAsync between the two transitions reads the bumped version after the first update.
///
/// Policy gate (spec 06 §6.1, T11a): after reaching Validating, <see cref="IValidationPolicy.CheckAsync"/>
/// is called. A non-null rejection reason → <c>Validating→Rejected</c> (terminal) + outbox
/// <c>notification.rejected.v1</c> + audit <c>operation.rejected</c> → <see cref="StepOutcome.Terminal"/>.
///
/// User-action gate (spec 06 §6.1, T7): if <see cref="IVerificationService.RequiresUserActionAsync"/>
/// returns true after Validating, transition Validating → AwaitingUserAction (reason
/// "user_action_required") + emit notification.user_action_required.v1 + audit → Wait.
/// The IInboxConsumerRequest handler ResumeFromUserActionCommand will re-enter validation
/// by emitting booking.command.requested.v1 (op back to Accepted, driver re-dispatches Validate).
///
/// Version-guarded updates: if any TryTransitionStatusAsync returns 0 rows (race), we return
/// Continue so the driver reloads and re-evaluates from the current state.
/// </summary>
public sealed class ValidateOperationCommandHandler(
    IBookingOperationRepository opRepo,
    IWorkflowStepRepository stepRepo,
    IVerificationService verificationService,
    IValidationPolicy validationPolicy,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    IUnitOfWork uow)
    : IRequestHandler<ValidateOperationCommand, StepOutcome>
{
    public async Task<StepOutcome> Handle(ValidateOperationCommand command, CancellationToken ct)
    {
        var op = await opRepo.GetAsync(command.OperationId, ct);

        if (op is null || op.TenantId != command.TenantId)
            return StepOutcome.Wait;

        if (op.Status is not (OperationStatus.Accepted or OperationStatus.Validating))
            return StepOutcome.Continue; // already advanced past this step

        var operationId = command.OperationId;
        var tenantId = command.TenantId;

        StepOutcome result = StepOutcome.Continue;

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            var current = await opRepo.GetAsync(operationId, ct2);
            if (current is null || current.TenantId != tenantId)
            {
                result = StepOutcome.Wait;
                return;
            }

            // Step 1: Accepted → Validating (skip if already Validating).
            if (current.Status == OperationStatus.Accepted)
            {
                var rows = await opRepo.TryTransitionStatusAsync(
                    tenantId, operationId, current.Version,
                    OperationStatus.Accepted, OperationStatus.Validating,
                    reason: "validating", ct: ct2);
                if (rows == 0)
                {
                    result = StepOutcome.Continue; // lost race → driver reloads
                    return;
                }
            }

            // Create-or-skip the "validate" workflow step (internal step, no external call).
            var existingStep = await stepRepo.GetByOperationAndTypeAsync(tenantId, operationId, "validate", ct2);
            if (existingStep is null)
            {
                var now = DateTimeOffset.UtcNow;
                await stepRepo.AddAsync(new WorkflowStep
                {
                    TenantId = tenantId,
                    WorkflowStepId = $"ws_{Guid.NewGuid():N}",
                    OperationId = operationId,
                    StepType = "validate",
                    Status = WorkflowStepStatus.Succeeded,
                    AttemptCount = 0,
                    Version = 0,
                    CreatedAt = now,
                    UpdatedAt = now,
                }, ct2);
            }

            // Step 2: Validating → ReservingCapacity.
            // Read fresh to get the current version (may have been bumped by step 1 above).
            var afterValidating = await opRepo.GetAsync(operationId, ct2);
            if (afterValidating is null || afterValidating.TenantId != tenantId)
            {
                result = StepOutcome.Continue;
                return;
            }

            if (afterValidating.Status == OperationStatus.Validating)
            {
                // T7: verification-grant gate → AwaitingUserAction when required.
                // Intentional order: user-action gate runs BEFORE the policy gate. Both are
                // re-evaluated on every re-drive (Validating is not terminal), so running the
                // policy after user-action is resolved is safe — the policy sees the completed
                // user data and can make a final accept/reject decision then.
                var requiresUserAction = await verificationService.RequiresUserActionAsync(tenantId, operationId, ct2);
                if (requiresUserAction)
                {
                    var rows2 = await opRepo.TryTransitionStatusAsync(
                        tenantId, operationId, afterValidating.Version,
                        OperationStatus.Validating, OperationStatus.AwaitingUserAction,
                        reason: "user_action_required", ct: ct2);

                    if (rows2 == 0)
                    {
                        result = StepOutcome.Continue;
                        return;
                    }

                    var now3 = DateTimeOffset.UtcNow;

                    await outboxRepo.AddAsync(new OutboxMessage
                    {
                        TenantId = tenantId,
                        MessageId = $"obx_{Guid.NewGuid():N}",
                        OperationId = operationId,
                        MessageType = "notification.user_action_required.v1",
                        CorrelationId = operationId,
                        Payload = JsonSerializer.Serialize(new
                        {
                            operation_id = operationId,
                            tenant_id = tenantId,
                        }),
                        OccurredAt = now3,
                    }, ct2);

                    await auditRepo.AddAsync(new AuditEvent
                    {
                        TenantId = tenantId,
                        EventId = $"evt_{Guid.NewGuid():N}",
                        EventType = "operation.user_action_required",
                        Actor = "system",
                        SubjectRef = operationId,
                        CorrelationId = operationId,
                        OperationId = operationId,
                        Metadata = JsonSerializer.Serialize(new { reason = "user_action_required" }),
                        OccurredAt = now3,
                    }, ct2);

                    result = StepOutcome.Wait;
                    return;
                }

                // T11a: policy gate → Validating→Rejected (terminal) when policy rejects.
                var rejectionReason = await validationPolicy.CheckAsync(tenantId, operationId, ct2);
                if (rejectionReason is not null)
                {
                    var rows4 = await opRepo.TryTransitionStatusAsync(
                        tenantId, operationId, afterValidating.Version,
                        OperationStatus.Validating, OperationStatus.Rejected,
                        reason: rejectionReason, ct: ct2);

                    if (rows4 == 0)
                    {
                        result = StepOutcome.Continue;
                        return;
                    }

                    var nowR = DateTimeOffset.UtcNow;

                    await outboxRepo.AddAsync(new OutboxMessage
                    {
                        TenantId = tenantId,
                        MessageId = $"obx_{Guid.NewGuid():N}",
                        OperationId = operationId,
                        MessageType = "notification.rejected.v1",
                        CorrelationId = operationId,
                        Payload = JsonSerializer.Serialize(new
                        {
                            operation_id = operationId,
                            tenant_id = tenantId,
                            reason = rejectionReason,
                        }),
                        OccurredAt = nowR,
                    }, ct2);

                    await auditRepo.AddAsync(new AuditEvent
                    {
                        TenantId = tenantId,
                        EventId = $"evt_{Guid.NewGuid():N}",
                        EventType = "operation.rejected",
                        Actor = "system",
                        SubjectRef = operationId,
                        CorrelationId = operationId,
                        OperationId = operationId,
                        Metadata = JsonSerializer.Serialize(new { reason = rejectionReason }),
                        OccurredAt = nowR,
                    }, ct2);

                    result = StepOutcome.Terminal;
                    return;
                }

                var rows3 = await opRepo.TryTransitionStatusAsync(
                    tenantId, operationId, afterValidating.Version,
                    OperationStatus.Validating, OperationStatus.ReservingCapacity,
                    reason: "validated", ct: ct2);
                if (rows3 == 0)
                {
                    result = StepOutcome.Continue;
                    return;
                }
            }

            result = StepOutcome.Continue;
        }, ct);

        return result;
    }
}
