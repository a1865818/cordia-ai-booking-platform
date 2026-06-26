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

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Optimistic-concurrency token (§22.1); default 0.</summary>
    public long Version { get; set; }
}
