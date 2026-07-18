using System.Text.Json;
using Cordia.Domain.Entities;

namespace Cordia.Application.Handlers.Waitlist;

/// <summary>
/// Builds the <c>waitlist.commands.match.v1</c> outbox message that re-offers a freed opening to the
/// next eligible enrollee (spec 11 §5.1) after a decline or expiry. The payload reconstructs the
/// match criteria from the resolved offer (slot id / start / seats) and its enrollment (location /
/// service). The matcher excludes enrollees who already received an offer for this slot, so re-match
/// advances rather than looping (see <see cref="Cordia.Domain.Interfaces.IWaitlistEnrollmentRepository.FindEligibleAsync"/>).
/// </summary>
public static class WaitlistRematchMessage
{
    /// <summary>The match message type (routes to the waitlist.processor consumer).</summary>
    public const string MessageType = "waitlist.commands.match.v1";

    /// <summary>
    /// Returns the re-match outbox message, or <c>null</c> when the opening cannot be reconstructed
    /// (no enrollment or no slot on the offer) — in which case there is nothing to re-offer.
    /// </summary>
    public static OutboxMessage? Build(
        string tenantId, WaitlistOffer offer, WaitlistEnrollment? enrollment, DateTimeOffset now)
    {
        if (enrollment is null || string.IsNullOrEmpty(offer.SlotId) || offer.SlotStartAt is null)
            return null;

        return new OutboxMessage
        {
            TenantId = tenantId,
            MessageId = $"obx_{Guid.NewGuid():N}",
            MessageType = MessageType,
            CorrelationId = offer.SlotId,
            Payload = JsonSerializer.Serialize(new
            {
                location_id = enrollment.LocationId,
                service_id = enrollment.ServiceId,
                slot_id = offer.SlotId,
                slot_start = offer.SlotStartAt,
                available_seats = offer.AvailableSeats,
            }),
            OccurredAt = now,
        };
    }
}
