using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Application.Handlers.Waitlist;

/// <summary>
/// Schedules a durable offer-expiry job for a waitlist offer (spec 11 §5.1). Mirrors
/// <c>CapacityExpiryScheduler</c>: inserts a <see cref="ScheduledJob"/> row
/// (JobType <c>"waitlist.commands.offer_expiry.v1"</c>, DestinationQueue <c>"waitlist.commands"</c>)
/// so the poller fires the expiry at or after <c>OfferExpiresAt</c>, routing it back to the
/// <c>waitlist.processor</c> consumer.
///
/// <para>MUST be called inside the caller's inbox/unit-of-work transaction so the job insert is
/// atomic with the offer transition (Inv-4). Deterministic <c>JobId = "sj_offerexp_{offerId}"</c>
/// makes the enqueue idempotent (re-drives produce exactly one job). The payload carries only the
/// offer id; the expiry handler reloads the offer (and its enrollment) to reconstruct the
/// matched-opening criteria for re-match (§5.1). No secrets (Inv-12).</para>
/// </summary>
public sealed class OfferExpiryScheduler(IScheduledJobRepository jobs)
{
    /// <summary>JobType emitted by this scheduler; also the registered consumer message type.</summary>
    public const string JobType = "waitlist.commands.offer_expiry.v1";

    /// <summary>Destination queue wire name (routes to the waitlist.processor consumer).</summary>
    public const string DestinationQueue = "waitlist.commands";

    public async Task ScheduleAsync(
        string tenantId,
        string offerId,
        DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        var jobId = $"sj_offerexp_{offerId}";

        if (await jobs.ExistsAsync(tenantId, jobId, ct))
            return;

        await jobs.AddAsync(new ScheduledJob
        {
            TenantId = tenantId,
            JobId = jobId,
            JobType = JobType,
            RunAfter = expiresAt,
            DestinationQueue = DestinationQueue,
            Status = ScheduledJobStatus.Pending,
            Attempt = 0,
            OperationId = null,
            Payload = JsonSerializer.Serialize(new { offer_id = offerId }),
        }, ct);
    }
}
