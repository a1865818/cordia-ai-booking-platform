using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Application.Handlers.BookingFulfillment.ReserveCapacity;

/// <summary>
/// Schedules a durable hold-expiry job for a first-party capacity hold (spec 07 §5, T5).
///
/// Inserts a <see cref="ScheduledJob"/> row
///   (JobType="booking.scheduled.hold_expiry.v1", DestinationQueue="booking.scheduled")
/// so that <c>ScheduledJobPollerHostedService</c> fires the expiry release at or after
/// <paramref name="expiresAt"/>.
///
/// MUST be called inside the caller's unit-of-work transaction so the job insertion is
/// atomic with the hold insert and operation status transition (Inv-4). Register as scoped.
/// </summary>
public sealed class CapacityExpiryScheduler(IScheduledJobRepository jobs)
{
    /// <summary>
    /// Inserts a scheduled hold-expiry job for the given hold. Must be called inside the
    /// ambient <c>IUnitOfWork.ExecuteInTransactionAsync</c> block — shares the scoped DbContext.
    /// <para>
    /// Uses a deterministic <c>JobId = "sj_holdexp_{holdId}"</c> so that re-drives enqueuing
    /// the same hold produce exactly one scheduled_job row (idempotent enqueue — FIX 6).
    /// If a job for this hold already exists (any status), this method is a no-op.
    /// </para>
    /// </summary>
    /// <param name="tenantId">Tenant owning the operation and hold.</param>
    /// <param name="operationId">Operation the hold belongs to.</param>
    /// <param name="holdId">ID of the active hold to expire.</param>
    /// <param name="expiresAt">When the expiry job should fire (hold TTL absolute timestamp).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task ScheduleAsync(
        string tenantId,
        string operationId,
        string holdId,
        DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        var jobId = $"sj_holdexp_{holdId}";

        // Idempotent enqueue: if a job for this hold already exists (e.g. re-drive), skip.
        if (await jobs.ExistsAsync(tenantId, jobId, ct))
            return;

        await jobs.AddAsync(new ScheduledJob
        {
            TenantId = tenantId,
            JobId = jobId,
            JobType = "booking.scheduled.hold_expiry.v1",
            RunAfter = expiresAt,
            DestinationQueue = "booking.scheduled",
            Status = ScheduledJobStatus.Pending,
            Attempt = 0,
            OperationId = operationId,
            Payload = JsonSerializer.Serialize(new
            {
                operation_id = operationId,
                hold_id = holdId,
            }),
        }, ct);
    }
}
