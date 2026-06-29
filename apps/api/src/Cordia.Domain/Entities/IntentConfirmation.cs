using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>Immutable confirmation evidence bound to exactly one intent revision (§8.8).</summary>
public class IntentConfirmation : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string ConfirmationId { get; set; } = string.Empty;

    public string IntentId { get; set; } = string.Empty;

    public int IntentRevision { get; set; }

    /// <summary>Must equal the bound revision's payload_hash (§8.8); enforced by trigger.</summary>
    public string IntentPayloadHash { get; set; } = string.Empty;

    public string IdentityRef { get; set; } = string.Empty;

    public string ConfirmationChannel { get; set; } = string.Empty;

    public string SourceTurnId { get; set; } = string.Empty;

    public string ReadbackVersion { get; set; } = string.Empty;

    /// <summary>Consent and policy disclosures shown, stored as jsonb. Defaults to a valid
    /// empty JSON array — an empty string is not valid jsonb and would fail on insert.</summary>
    public string Disclosures { get; set; } = "[]";

    public VerificationLevel VerificationLevel { get; set; }

    public DateTimeOffset ConfirmedAt { get; set; }

    /// <summary>Navigation property to the exact revision this confirmation binds.</summary>
    public BookingIntentRevision Revision { get; set; } = null!;
}
