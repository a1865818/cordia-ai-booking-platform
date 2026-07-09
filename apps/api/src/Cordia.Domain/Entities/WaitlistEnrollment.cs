using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>
/// Customer enrollment on the waitlist. PK (tenant_id, enrollment_id).
/// DepositTerms and AutoBook are stored as jsonb.
/// Optimistic-concurrency version token (§22.1).
/// </summary>
public class WaitlistEnrollment : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string EnrollmentId { get; set; } = string.Empty;

    public string CustomerRef { get; set; } = string.Empty;

    public DateTimeOffset AcceptableFrom { get; set; }

    public DateTimeOffset AcceptableTo { get; set; }

    public int PartySize { get; set; }

    public string LocationId { get; set; } = string.Empty;

    public string ServiceId { get; set; } = string.Empty;

    public int OfferLifetimeSeconds { get; set; }

    /// <summary>jsonb — deposit terms configuration; null when no deposit required.</summary>
    public string? DepositTerms { get; set; }

    public string NotificationChannel { get; set; } = string.Empty;

    /// <summary>jsonb — auto-book configuration; null when auto-book disabled.</summary>
    public string? AutoBook { get; set; }

    /// <summary>
    /// Authenticated customer identity captured at enroll time (spec 11 §5.1, divergence #1). Stamped
    /// onto the <c>IntentConfirmation</c> when the offer is accepted, so a waitlist accept mints a real
    /// confirmed revision tied to THIS customer (Inv-1) — not to whoever merely saw the offer
    /// notification. Defaults to <see cref="CustomerRef"/> when not separately supplied.
    /// </summary>
    public string IdentityRef { get; set; } = string.Empty;

    /// <summary>
    /// Verification level established when the customer enrolled (§8.8 vocabulary:
    /// <c>unverified | channel_verified | identity_verified | high_assurance</c>). Carried onto the
    /// acceptance confirmation as the "verified follow-up" assurance (§5.1); defaults to
    /// <c>unverified</c> so the accept never over-claims identity assurance.
    /// </summary>
    public string VerificationLevel { get; set; } = "unverified";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the enrollment was cancelled (Inv-6, terminal); null while active. Cancelled enrollments are never matched.</summary>
    public DateTimeOffset? CancelledAt { get; set; }

    /// <summary>Optimistic-concurrency token (§22.1); default 0.</summary>
    public long Version { get; set; }
}
