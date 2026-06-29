using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Application.Handlers.BookingFulfillment;

/// <summary>
/// Schedules a durable retry for a workflow step (spec 06 §15.2, §6.3 durable schedule).
///
/// Inserts a <see cref="ScheduledJob"/> row
///   (JobType="booking.command.requested.v1", DestinationQueue="booking.commands")
/// and sets the step's <c>NotBefore</c> to <paramref name="nextEligibleAt"/> so the step
/// sweeper does not re-enqueue the step before the scheduled retry fires.
///
/// MUST be called inside the caller's unit-of-work transaction so the job insertion and
/// the NotBefore update are atomic with the attempt outcome write.
/// Register as scoped.
/// </summary>
public sealed class RetryScheduler(
    IScheduledJobRepository jobs,
    IWorkflowStepRepository steps)
{
    /// <summary>
    /// Schedules a durable retry: inserts a scheduled_job and gates the workflow step with
    /// <c>NotBefore=nextEligibleAt</c>.
    /// </summary>
    /// <param name="tenantId">Tenant owning the operation.</param>
    /// <param name="operationId">The operation to retry.</param>
    /// <param name="workflowStepId">The workflow step that should not be swept before the retry.</param>
    /// <param name="stepType">Human-readable step label stored in the job payload.</param>
    /// <param name="nextEligibleAt">When the retry should fire (full-jitter from <see cref="IRetryPolicy"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ScheduleAsync(
        string tenantId,
        string operationId,
        string workflowStepId,
        string stepType,
        DateTimeOffset nextEligibleAt,
        CancellationToken ct)
    {
        var jobId = $"sj_{Guid.NewGuid():N}";

        await jobs.AddAsync(new ScheduledJob
        {
            TenantId = tenantId,
            JobId = jobId,
            // Open job type label; resolves to the booking driver via ConsumerRegistry (§6.2).
            JobType = "booking.command.requested.v1",
            RunAfter = nextEligibleAt,
            WorkflowStepId = workflowStepId,
            // Wire name used by ScheduledJobPollerHostedService → LogicalQueueRegistry lookup.
            DestinationQueue = "booking.commands",
            Status = ScheduledJobStatus.Pending,
            Attempt = 0,
            OperationId = operationId,
            Payload = JsonSerializer.Serialize(new
            {
                operation_id = operationId,
                step_type = stepType,
            }),
        }, ct);

        // Gate the step sweeper: NotBefore prevents re-enqueue before the scheduled job fires.
        // This does NOT bump Version (§15.2).
        await steps.SetNotBeforeAsync(tenantId, workflowStepId, nextEligibleAt, ct);
    }
}
