using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Waitlist.ReconcileAccept;

/// <summary>
/// Probes the outcome of a booking minted by a waitlist accept whose offer claim was lost, and settles
/// the waitlist state so the enrollee is never double-offered (spec 11 §5.1). See
/// <see cref="ReconcileWaitlistAcceptCommand"/> for the race this closes.
///
/// <para><b>Runs inside the inbox transaction</b> — never opens its own. State-driven and idempotent:</para>
/// <list type="bullet">
/// <item><b>Operation Booked</b> → the enrollee is genuinely booked. Mark the enrollment terminal
/// (fulfilled) so <c>FindEligibleAsync</c> excludes it — no re-offer, no double-book.</item>
/// <item><b>Operation terminally failed</b> (the released hold could not be re-reserved, etc.) → nothing
/// was booked, so the enrollee correctly stays eligible; settle.</item>
/// <item><b>Operation still in-flight</b> → re-schedule one more bounded probe; after
/// <see cref="WaitlistAcceptReconcileScheduler.MaxAttempts"/> attempts, log and settle (06 will still
/// emit its own terminal outcome + notification).</item>
/// </list>
/// </summary>
public sealed class ReconcileWaitlistAcceptCommandHandler(
    IBookingOperationRepository opRepo,
    IWaitlistEnrollmentRepository enrollmentRepo,
    IAuditEventRepository auditRepo,
    IOperationWorkItemRepository workItemRepo,
    WaitlistAcceptReconcileScheduler reconcileScheduler,
    ILogger<ReconcileWaitlistAcceptCommandHandler> logger)
    : IRequestHandler<ReconcileWaitlistAcceptCommand, Unit>
{
    /// <summary>
    /// Stop states that are NOT in spec 06's terminal set but are terminal for a DELEGATED WAITLIST
    /// BOOKING specifically — probing them again can never change the answer.
    ///
    /// <para><c>AlternativesRequired</c>: 06 treats it as a wait state because a guest can choose an
    /// alternative, but nobody is going to choose on behalf of a waitlist accept — it will sit there
    /// until it expires. Nothing was booked, so the enrollee correctly stays eligible.</para>
    ///
    /// <para><c>ManualReview</c>: an operator owns it now. <c>AdvanceBookingOperationCommandHandler</c>
    /// has no dispatch case for it (T10), so it cannot progress on its own and re-probing is pure
    /// waste. It is reached from here when a compensating provider-cancel comes back
    /// ambiguous/transient/permanent — i.e. exactly the path a cancel racing this accept produces.</para>
    ///
    /// <para>Deliberately a SHORT list of documented exceptions, not a second copy of the terminal
    /// set: whether 06 has finished is asked of <see cref="OperationStateMachine.IsTerminal"/>.</para>
    /// </summary>
    private static readonly HashSet<OperationStatus> _waitlistStopStates =
    [
        OperationStatus.AlternativesRequired,
        OperationStatus.ManualReview,
    ];

    /// <summary>
    /// Has 06 finished with this operation, one way or another?
    ///
    /// <para><b>Asked of the state machine, not of a private list.</b> This handler used to keep its
    /// own <c>{FailedFinal, Rejected, Expired, AlternativesRequired}</c> set and classify everything
    /// else as "still in flight" by exclusion. Spec 06 owns <c>operation_status</c> and its terminal
    /// semantics (single-ownership registry) and already exposes
    /// <see cref="OperationStateMachine.IsTerminal"/> — the same authority
    /// <c>AdvanceBookingOperationCommandHandler</c> and <c>EvaluateOperationSlaCommand</c> use. A
    /// second, narrower copy could only ever drift from it, and it did: <c>CompensationRequired</c>
    /// and <c>ManualReview</c> appeared in neither list, so both were treated as healthy progress
    /// and re-probed until the attempt budget ran out.</para>
    ///
    /// <para><c>CompensationRequired</c> is deliberately NOT forced terminal here. It has legal
    /// outgoing edges and the driver dispatches it to compensation in the very next iteration of the
    /// same loop, so it genuinely does resolve — usually within milliseconds, long before the first
    /// probe. Treating it as an ending would settle the waitlist on a state the operation is
    /// actively leaving.</para>
    /// </summary>
    private static bool HasSettled(OperationStatus status)
        => OperationStateMachine.IsTerminal(status) || _waitlistStopStates.Contains(status);

    public async Task<Unit> Handle(ReconcileWaitlistAcceptCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        var op = await opRepo.GetAsync(command.OperationId, ct);
        if (op is null || op.TenantId != command.TenantId)
        {
            logger.LogWarning(
                "Waitlist accept-reconcile: operation {OperationId} not found; settling.", command.OperationId);
            return Unit.Value;
        }

        if (op.Status == OperationStatus.Booked)
        {
            // The booking landed — the enrollee got their waitlist wish. Mark the enrollment terminal so
            // it is never re-offered (FindEligibleAsync excludes cancelled enrollments). Idempotent: an
            // already-terminal enrollment settles.
            var enrollment = await enrollmentRepo.GetAsync(command.EnrollmentId, ct);
            if (enrollment is not null && enrollment.CancelledAt is null)
            {
                await enrollmentRepo.CancelAsync(
                    command.TenantId, command.EnrollmentId, enrollment.Version, now, ct);
                await AuditAsync(command, "waitlist.enrollment_fulfilled", op.Status, now, ct);
            }
            return Unit.Value;
        }

        if (HasSettled(op.Status))
        {
            // The mint did not produce a booking (e.g. the released hold could not be re-reserved). No
            // compensation needed — the enrollee correctly remains eligible for a future opening.
            //
            // `ManualReview` lands here too, and the enrollment still stays eligible: the booking's
            // outcome is genuinely unknown until an operator resolves it, and of the two ways to be
            // wrong, leaving the guest eligible costs at worst a second offer, while stamping
            // fulfilment for a booking that later fails strands them with nothing (Inv-16). The audit
            // row carries the status, so the operator resolving the review can see the waitlist
            // consequence rather than having to infer it.
            await AuditAsync(command, "waitlist.accept_reconcile_settled", op.Status, now, ct);
            return Unit.Value;
        }

        // Still in-flight — re-check after another delay, bounded so we never schedule forever.
        //
        // RESIDUAL (accepted, spec 11 §12): this reconcile narrows but does NOT fully close the
        // cross-slot double-offer window — and the window applies to EVERY accept/auto-accept
        // (claim won or lost; both schedule this probe), not only the lost-claim race. Between the
        // mint and the probe observing the op as Booked (up to Delay x MaxAttempts, ~15 min at the
        // defaults), the enrollee's Accepted offer does not exclude them from FindEligibleAsync, so
        // an INDEPENDENT slot opening can offer — or, under an AutoBookGrant, silently book — them
        // a second time inside that window. The trade-off is deliberate: we favour eligibility and
        // reconcile off the real 06 outcome rather than eagerly excluding a not-yet-booked enrollee
        // (which would strand them if 06 later fails capacity/payment). To fully close it, a future
        // spec would add an "accepted-offer / pending-booking" exclusion to FindEligibleAsync that
        // is itself released on a terminal-failure outcome.
        var nextAttempt = command.Attempt + 1;
        if (nextAttempt >= WaitlistAcceptReconcileScheduler.MaxAttempts)
        {
            logger.LogWarning(
                "Waitlist accept-reconcile: operation {OperationId} still {Status} after {Attempts} attempts; "
                + "settling (06 will emit its own terminal outcome).",
                command.OperationId, op.Status, nextAttempt);

            // Audited, not merely logged.
            //
            // Giving up leaves the enrollment neither fulfilled nor explicitly re-matched, which is
            // a real decision about a guest — and a log line is not a record anyone can query later.
            // Every other outcome of this handler writes an audit row; the one that abandons the
            // reconcile wrote nothing at all, so an enrollment could be quietly left in limbo with
            // no trace that the platform had stopped watching.
            await AuditAsync(command, "waitlist.accept_reconcile_abandoned", op.Status, now, ct);

            // …and handed to a human, because an audit row is history, not a queue.
            //
            // The audit made abandonment traceable AFTER someone suspected it. It is not something
            // anyone reads proactively: it carries no SLA deadline, no assignee, and it is not
            // counted by the manual-review depth gauge. The platform has just declared that it has
            // stopped watching a guest's booking whose outcome it does not know — that is precisely
            // the definition of work for a person, so it opens a work item like every other
            // give-up path in the system does.
            //
            // TryAddOpenAsync, not AddAsync: the operation may ALREADY have an open work item (this
            // is reached when the op is stuck in-flight, and the SLA sweeper or a ManualReview
            // escalation may have opened one first). A lost race must be a benign no-op, not a
            // 23505 that aborts the inbox transaction and poisons the message.
            await workItemRepo.TryAddOpenAsync(new OperationWorkItem
            {
                TenantId = command.TenantId,
                WorkItemId = $"wi_{Guid.NewGuid():N}",
                OperationId = command.OperationId,
                Reason = $"waitlist_accept_reconcile_abandoned:{command.EnrollmentId}",
                State = "open",
                SlaDeadline = now.AddHours(1),
                CreatedAt = now,
                Version = 0,
            }, ct);

            return Unit.Value;
        }

        await reconcileScheduler.ScheduleAsync(
            command.TenantId, command.OfferId, command.OperationId, command.EnrollmentId, nextAttempt, now, ct);
        return Unit.Value;
    }

    private Task AuditAsync(
        ReconcileWaitlistAcceptCommand command, string eventType, OperationStatus opStatus,
        DateTimeOffset now, CancellationToken ct)
        => auditRepo.AddAsync(new AuditEvent
        {
            TenantId = command.TenantId,
            EventId = $"evt_{Guid.NewGuid():N}",
            EventType = eventType,
            Actor = "system",
            SubjectRef = command.OfferId,
            CorrelationId = command.OfferId,
            OperationId = command.OperationId,
            Metadata = JsonSerializer.Serialize(new
            {
                offer_id = command.OfferId,
                operation_id = command.OperationId,
                enrollment_id = command.EnrollmentId,
                op_status = opStatus.ToString(),
            }),
            OccurredAt = now,
        }, ct);
}
