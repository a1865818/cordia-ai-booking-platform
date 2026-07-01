using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Application.Handlers.Waitlist.ReconcileAccept;

/// <summary>
/// Schedules a delayed <see cref="ReconcileWaitlistAcceptCommand"/> for a waitlist offer whose accept
/// lost the offer claim after minting the booking (spec 11 §5.1). Mirrors
/// <see cref="OfferExpiryScheduler"/>: inserts a <see cref="ScheduledJob"/> row (JobType
/// <c>waitlist.commands.reconcile_accept.v1</c>, DestinationQueue <c>waitlist.commands</c>) so the
/// poller delivers the reconcile back to the <c>waitlist.processor</c> consumer after
/// <see cref="Delay"/> — long enough for the spec-06 engine to finish the operation before it is probed.
///
/// <para>The job id embeds the attempt (<c>sj_wlreconcile_{offerId}_{attempt}</c>) so a bounded
/// re-check (when the operation is still in-flight at probe time) can enqueue a fresh job while the
/// first-attempt schedule stays idempotent under duplicate delivery. Must be called inside the caller's
/// transaction so the job insert commits atomically with the surrounding work (Inv-4).</para>
/// </summary>
public sealed class WaitlistAcceptReconcileScheduler(IScheduledJobRepository jobs)
{
    /// <summary>Delay before each reconcile probe — comfortably longer than a typical 06 booking.</summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMinutes(5);

    /// <summary>Maximum number of probe attempts before giving up (a still-in-flight op is escalated by log).</summary>
    public const int MaxAttempts = 3;

    public async Task ScheduleAsync(
        string tenantId,
        string offerId,
        string operationId,
        string enrollmentId,
        int attempt,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var jobId = $"sj_wlreconcile_{offerId}_{attempt}";

        // Idempotent per (offer, attempt): a duplicate accept delivery or reconcile re-entry does not
        // stack duplicate jobs.
        if (await jobs.ExistsAsync(tenantId, jobId, ct))
            return;

        await jobs.AddAsync(new ScheduledJob
        {
            TenantId = tenantId,
            JobId = jobId,
            JobType = ReconcileWaitlistAcceptCommand.MessageType,
            RunAfter = now + Delay,
            DestinationQueue = OfferExpiryScheduler.DestinationQueue, // "waitlist.commands"
            Status = ScheduledJobStatus.Pending,
            Attempt = 0,
            OperationId = operationId,
            Payload = JsonSerializer.Serialize(new
            {
                offer_id = offerId,
                operation_id = operationId,
                enrollment_id = enrollmentId,
                attempt,
            }),
        }, ct);
    }
}
