using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>
/// Offer issued from the waitlist to a customer. PK (tenant_id, offer_id).
/// FK (tenant_id, enrollment_id) → waitlist_enrollments ON DELETE RESTRICT.
/// Optimistic-concurrency version token (§22.1).
/// </summary>
public class WaitlistOffer : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string OfferId { get; set; } = string.Empty;

    public string EnrollmentId { get; set; } = string.Empty;

    public string? HoldId { get; set; }

    public string? OperationId { get; set; }

    /// <summary>First-party slot reserved for this offer (the freed opening); needed to re-reserve on re-match.</summary>
    public string? SlotId { get; set; }

    /// <summary>Start time of the matched slot; carried into the booking + the offer notification.</summary>
    public DateTimeOffset? SlotStartAt { get; set; }

    /// <summary>Seats available in the matched opening; the re-match criteria after a decline/expiry.</summary>
    public int AvailableSeats { get; set; }

    public WaitlistOfferStatus Status { get; set; }

    public DateTimeOffset? OfferExpiresAt { get; set; }

    public DateTimeOffset? MatchedAt { get; set; }

    public DateTimeOffset? OfferedAt { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Optimistic-concurrency token (§22.1); default 0.</summary>
    public long Version { get; set; }
}
