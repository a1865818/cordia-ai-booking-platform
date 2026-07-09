using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Preorder.CompensateBooking;

/// <summary>
/// Escalates a post-commit pre-order failure to human review (spec 11 §5.3, §21.4 / §8 row 10).
///
/// <para>Under the booking-delegated design (divergence #2) the spec-06 engine owns capacity (07),
/// payment (09), and the provider booking; once it signals <c>booking_committed</c> the booking is a
/// real, committed record. If the saga cannot finish binding the pre-order after that point, the
/// correct action is NOT to auto-void payment or cancel the booking (06 owns those and a committed
/// booking must be preserved) — it is to park the saga in <c>ManualReview</c> so an operator + the
/// customer decide (§21.4). This handler therefore no longer takes payment/item ports; it simply
/// advances <c>CompensatingBooking → ManualReview</c> and emits the single combined failure outcome
/// (Inv-8).</para>
///
/// <para>Producer: <c>ReconcilePreorderSagaCommandHandler</c> escalates a saga stuck in
/// <c>CommittingPreorder</c> past its escalation window to <c>CompensatingBooking</c> and enqueues
/// this step (§8 FM#10 — "if pre-order still impossible → ManualReview"). Runs in the inbox
/// transaction; settles when the saga is missing or no longer in CompensatingBooking (Inv-5/Inv-9).</para>
///
/// <para><b>Inv-8 — parking is not a terminal outcome.</b> The park emits only a NEUTRAL
/// "still confirming / under review" customer notice, not <c>notification.booking_failed.v1</c>: the
/// booking is committed, so a "failed" notification here would contradict the confirmed outcome a
/// later convert-to-standalone emits. The single combined outcome is owned by the terminal resolution
/// (convert → confirmed, cancel → failed) so the customer receives exactly one authoritative outcome.</para>
/// </summary>
public sealed class CompensatePreorderBookingCommandHandler(
    IPreorderSagaRepository sagaRepo,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    ILogger<CompensatePreorderBookingCommandHandler> logger)
    : IRequestHandler<CompensatePreorderBookingCommand, Unit>
{
    public async Task<Unit> Handle(CompensatePreorderBookingCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var saga = await sagaRepo.GetAsync(command.SagaId, ct);
        if (saga is null || saga.TenantId != command.TenantId || saga.SagaState != PreorderSagaState.CompensatingBooking)
        {
            logger.LogInformation("Preorder compensate-booking: saga {SagaId} not in CompensatingBooking; settling.", command.SagaId);
            return Unit.Value;
        }

        // Preserve the committed booking (§21.4) — escalate to a human rather than auto-reversing.
        var won = await sagaRepo.TryAdvanceStateAsync(
            command.TenantId, command.SagaId, saga.Version,
            PreorderSagaState.CompensatingBooking, PreorderSagaState.ManualReview, now,
            setFailureReason: saga.FailureReason ?? "manual_review_post_commit", ct: ct);
        if (won == 0)
            return Unit.Value;

        // Inv-8: emit ONLY a neutral "under review" notice — never a terminal failure outcome (the
        // booking is committed). The single combined outcome is owned by the terminal convert/cancel.
        await outboxRepo.AddAsync(PreorderSagaSupport.InReviewNotice(
            command.TenantId, command.SagaId, command.OperationId, now, command.CorrelationId), ct);
        await auditRepo.AddAsync(PreorderSagaSupport.Audit(
            command.TenantId, "preorder.manual_review", command.SagaId, command.OperationId, now,
            new { saga_id = command.SagaId, reason = "post_commit_failure" }), ct);
        return Unit.Value;
    }
}
