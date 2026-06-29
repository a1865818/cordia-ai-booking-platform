using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>Immutable revision snapshot of a booking intent's canonical payload.</summary>
public class BookingIntentRevision : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string IntentId { get; set; } = string.Empty;

    public int Revision { get; set; }

    /// <summary>Canonical booking payload stored as jsonb. Defaults to a valid empty
    /// JSON object — an empty string is not valid jsonb and would fail on insert.</summary>
    public string CanonicalPayload { get; set; } = "{}";

    public string PayloadHash { get; set; } = string.Empty;

    public string SchemaVersion { get; set; } = string.Empty;

    public string TenantPolicyVersion { get; set; } = string.Empty;

    public int? PriorRevision { get; set; }

    public string? SupersessionReason { get; set; }

    public string? SourceSessionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Navigation property to parent intent.</summary>
    public BookingIntent Intent { get; set; } = null!;
}
