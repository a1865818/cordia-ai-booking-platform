using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.RequestPayment;

/// <summary>
/// Implements the Held → AwaitingPayment / Held → Finalizing decision seam (spec 06 §6.1, §10).
///
/// Routes:
///   Deposit required  → Held → AwaitingPayment: create DepositSession + request_payment step
///                        (Succeeded) + outbox payment.deposit_link.requested.v1 + audit → Wait.
///   Deposit not required → Held → Finalizing: create request_payment step (Succeeded) → Continue
///                          (driver picks up FinalizeBookingCommand on the next loop iteration).
///
/// The sweeper MUST NOT re-drive an AwaitingPayment operation — the request_payment step is
/// marked Succeeded so the sweeper sees the step is complete and does not re-dispatch.
/// </summary>
public sealed class RequestPaymentCommandHandler(
    IBookingOperationRepository opRepo,
    IDepositPolicy depositPolicy,
    IDepositSessionRepository depositSessionRepo,
    IWorkflowStepRepository stepRepo,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    IUnitOfWork uow)
    : IRequestHandler<RequestPaymentCommand, StepOutcome>
{
    private const int DepositWindowMinutes = 30;
    private const string StepType = "request_payment";

    public async Task<StepOutcome> Handle(RequestPaymentCommand command, CancellationToken ct)
    {
        var op = await opRepo.GetAsync(command.OperationId, ct);

        if (op is null || op.TenantId != command.TenantId)
            return StepOutcome.Wait;

        if (op.Status != OperationStatus.Held)
            return StepOutcome.Continue; // already advanced past this step

        var tenantId = command.TenantId;
        var operationId = command.OperationId;

        var req = await depositPolicy.GetRequirementAsync(tenantId, operationId, ct);

        StepOutcome result = StepOutcome.Continue;

        if (!req.Required)
        {
            // No-deposit fast path: Held → Finalizing in one transaction.
            await uow.ExecuteInTransactionAsync(async ct2 =>
            {
                var current = await opRepo.GetAsync(operationId, ct2);
                if (current is null || current.TenantId != tenantId || current.Status != OperationStatus.Held)
                {
                    result = StepOutcome.Continue;
                    return;
                }

                // Create-or-skip request_payment step (Succeeded immediately — no external call).
                var existingStep = await stepRepo.GetByOperationAndTypeAsync(tenantId, operationId, StepType, ct2);
                if (existingStep is null)
                {
                    var now2 = DateTimeOffset.UtcNow;
                    await stepRepo.AddAsync(new WorkflowStep
                    {
                        TenantId = tenantId,
                        WorkflowStepId = $"ws_{Guid.NewGuid():N}",
                        OperationId = operationId,
                        StepType = StepType,
                        Status = WorkflowStepStatus.Succeeded,
                        AttemptCount = 0,
                        Version = 0,
                        CreatedAt = now2,
                        UpdatedAt = now2,
                    }, ct2);
                }

                var rows = await opRepo.TryTransitionStatusAsync(
                    tenantId, operationId, current.Version,
                    OperationStatus.Held, OperationStatus.Finalizing,
                    reason: "finalizing_no_deposit", ct: ct2);

                // Won or lost the Held→Finalizing race, the driver reloads and re-evaluates either way.
                _ = rows;
                result = StepOutcome.Continue;
            }, ct);

            return result;
        }

        // Deposit required path: Held → AwaitingPayment.
        // FIX (C5): Perform the version-guarded Held→AwaitingPayment CAS FIRST.  Only if the CAS
        // wins (rows > 0) do we create the DepositSession, outbox, and audit — this ensures a
        // lost-race tick does not persist a DepositSession row that would cause a unique-violation
        // on the next re-drive.  The request_payment step is created/skipped before the CAS (it is
        // idempotent via the existing get-or-create) so it is always in place if needed.
        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            var current = await opRepo.GetAsync(operationId, ct2);
            if (current is null || current.TenantId != tenantId || current.Status != OperationStatus.Held)
            {
                result = StepOutcome.Continue;
                return;
            }

            var now = DateTimeOffset.UtcNow;

            // Create-or-skip request_payment step (Succeeded — the LINK request; waiting for payment
            // is external so the sweeper must not re-drive an AwaitingPayment operation).
            var existingStep = await stepRepo.GetByOperationAndTypeAsync(tenantId, operationId, StepType, ct2);
            if (existingStep is null)
            {
                await stepRepo.AddAsync(new WorkflowStep
                {
                    TenantId = tenantId,
                    WorkflowStepId = $"ws_{Guid.NewGuid():N}",
                    OperationId = operationId,
                    StepType = StepType,
                    Status = WorkflowStepStatus.Succeeded,
                    AttemptCount = 0,
                    Version = 0,
                    CreatedAt = now,
                    UpdatedAt = now,
                }, ct2);
            }

            // Held → AwaitingPayment (version-guarded CAS — must happen BEFORE DepositSession).
            var rows = await opRepo.TryTransitionStatusAsync(
                tenantId, operationId, current.Version,
                OperationStatus.Held, OperationStatus.AwaitingPayment,
                reason: "awaiting_payment", ct: ct2);

            if (rows == 0)
            {
                result = StepOutcome.Continue; // lost race → driver reloads; no DepositSession created
                return;
            }

            // CAS won — now create the DepositSession (unique constraint prevents duplicates per operation).
            var depositSessionId = $"dep_{Guid.NewGuid():N}";
            await depositSessionRepo.AddAsync(new DepositSession
            {
                TenantId = tenantId,
                DepositSessionId = depositSessionId,
                OperationId = operationId,
                AmountMinor = req.AmountMinor,
                Currency = req.Currency,
                Status = DepositStatus.Created,
                ExpiresAt = now.AddMinutes(DepositWindowMinutes),
                CreatedAt = now,
                Version = 0,
            }, ct2);

            // Outbox: spec 09 consumes this to create the Stripe link.
            await outboxRepo.AddAsync(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = $"obx_{Guid.NewGuid():N}",
                OperationId = operationId,
                MessageType = "payment.deposit_link.requested.v1",
                CorrelationId = operationId,
                Payload = JsonSerializer.Serialize(new
                {
                    operation_id = operationId,
                    deposit_session_id = depositSessionId,
                    amount_minor = req.AmountMinor,
                    currency = req.Currency,
                    tenant_id = tenantId,
                }),
                OccurredAt = now,
            }, ct2);

            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = tenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "operation.awaiting_payment",
                Actor = "system",
                SubjectRef = operationId,
                CorrelationId = operationId,
                OperationId = operationId,
                Metadata = JsonSerializer.Serialize(new
                {
                    deposit_session_id = depositSessionId,
                    amount_minor = req.AmountMinor,
                    currency = req.Currency,
                }),
                OccurredAt = now,
            }, ct2);

            result = StepOutcome.Wait;
        }, ct);

        return result;
    }
}
