using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.ReserveCapacity;

/// <summary>
/// Handles capacity reservation for a booking operation (spec 06 §6.2, reserve_capacity step).
///
/// Routes:
///   <see cref="CapacityResult.Reserved"/>   → ReservingCapacity → Held (reason "held").
///   <see cref="CapacityResult.NoCapacity"/> → ReservingCapacity → AlternativesRequired
///                                              (FM#8, reason "no_capacity") + outbox
///                                              notification.alternatives_required.v1 + audit.
///
/// TODO(spec 07): replace <see cref="ICapacityService"/> impl with the atomic conditional
/// capacity write / external hold primitive.
/// </summary>
public sealed class ReserveCapacityCommandHandler(
    IBookingOperationRepository opRepo,
    IWorkflowStepRepository stepRepo,
    ICapacityService capacityService,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    IUnitOfWork uow)
    : IRequestHandler<ReserveCapacityCommand, StepOutcome>
{
    public async Task<StepOutcome> Handle(ReserveCapacityCommand command, CancellationToken ct)
    {
        var op = await opRepo.GetAsync(command.OperationId, ct);

        if (op is null || op.TenantId != command.TenantId)
            return StepOutcome.Wait;

        if (op.Status != OperationStatus.ReservingCapacity)
            return StepOutcome.Continue; // already advanced past this step

        var operationId = command.OperationId;
        var tenantId = command.TenantId;

        // Call the capacity seam outside the transaction (stateless check; spec 07 will make
        // this a conditional write inside a txn with the rest of the reservation).
        var capacityResult = await capacityService.TryReserveAsync(tenantId, operationId, ct);

        StepOutcome result = StepOutcome.Continue;

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            // Create-or-skip the "reserve_capacity" workflow step.
            var existingStep = await stepRepo.GetByOperationAndTypeAsync(tenantId, operationId, "reserve_capacity", ct2);
            if (existingStep is null)
            {
                var now2 = DateTimeOffset.UtcNow;
                await stepRepo.AddAsync(new WorkflowStep
                {
                    TenantId = tenantId,
                    WorkflowStepId = $"ws_{Guid.NewGuid():N}",
                    OperationId = operationId,
                    StepType = "reserve_capacity",
                    Status = WorkflowStepStatus.Succeeded,
                    AttemptCount = 0,
                    Version = 0,
                    CreatedAt = now2,
                    UpdatedAt = now2,
                }, ct2);
            }

            var current = await opRepo.GetAsync(operationId, ct2);
            if (current is null || current.TenantId != tenantId || current.Status != OperationStatus.ReservingCapacity)
            {
                result = StepOutcome.Continue;
                return;
            }

            if (capacityResult == CapacityResult.Reserved)
            {
                // Happy path: advance to Held.
                _ = await opRepo.TryTransitionStatusAsync(
                    tenantId, operationId, current.Version,
                    OperationStatus.ReservingCapacity, OperationStatus.Held,
                    reason: "held", ct: ct2);

                result = StepOutcome.Continue;
            }
            else
            {
                // FM#8 — no capacity: advance to AlternativesRequired.
                var rows = await opRepo.TryTransitionStatusAsync(
                    tenantId, operationId, current.Version,
                    OperationStatus.ReservingCapacity, OperationStatus.AlternativesRequired,
                    reason: "no_capacity", ct: ct2);

                if (rows > 0)
                {
                    var now = DateTimeOffset.UtcNow;

                    await outboxRepo.AddAsync(new OutboxMessage
                    {
                        TenantId = tenantId,
                        MessageId = $"obx_{Guid.NewGuid():N}",
                        OperationId = operationId,
                        MessageType = "notification.alternatives_required.v1",
                        CorrelationId = operationId,
                        Payload = JsonSerializer.Serialize(new
                        {
                            operation_id = operationId,
                            tenant_id = tenantId,
                        }),
                        OccurredAt = now,
                    }, ct2);

                    await auditRepo.AddAsync(new AuditEvent
                    {
                        TenantId = tenantId,
                        EventId = $"evt_{Guid.NewGuid():N}",
                        EventType = "operation.alternatives_required",
                        Actor = "system",
                        SubjectRef = operationId,
                        CorrelationId = operationId,
                        OperationId = operationId,
                        Metadata = JsonSerializer.Serialize(new { reason = "no_capacity" }),
                        OccurredAt = now,
                    }, ct2);
                }

                // AlternativesRequired requires external user input — stop and wait.
                // But if rows==0 we lost the race; return Continue so the driver reloads
                // and re-evaluates the current state (C13).
                result = rows > 0 ? StepOutcome.Wait : StepOutcome.Continue;
            }
        }, ct);

        return result;
    }
}
