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
    WaitlistAcceptReconcileScheduler reconcileScheduler,
    ILogger<ReconcileWaitlistAcceptCommandHandler> logger)
    : IRequestHandler<ReconcileWaitlistAcceptCommand, Unit>
{
    /// <summary>06 operation states that are a terminal FAILURE for a delegated waitlist booking.</summary>
    private static readonly HashSet<OperationStatus> _terminalFailureStates =
    [
        OperationStatus.FailedFinal,
        OperationStatus.Rejected,
        OperationStatus.Expired,
        OperationStatus.AlternativesRequired,
    ];

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

        if (_terminalFailureStates.Contains(op.Status))
        {
            // The mint did not produce a booking (e.g. the released hold could not be re-reserved). No
            // compensation needed — the enrollee correctly remains eligible for a future opening.
            await AuditAsync(command, "waitlist.accept_reconcile_settled", op.Status, now, ct);
            return Unit.Value;
        }

        // Still in-flight — re-check after another delay, bounded so we never schedule forever.
        var nextAttempt = command.Attempt + 1;
        if (nextAttempt >= WaitlistAcceptReconcileScheduler.MaxAttempts)
        {
            logger.LogWarning(
                "Waitlist accept-reconcile: operation {OperationId} still {Status} after {Attempts} attempts; "
                + "settling (06 will emit its own terminal outcome).",
                command.OperationId, op.Status, nextAttempt);
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
