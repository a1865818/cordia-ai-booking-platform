using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Observability;

namespace Cordia.Domain.Entities;

/// <summary>Immutable confirmation evidence bound to exactly one intent revision (§8.8).</summary>
public class IntentConfirmation : ITenantScoped, ICorrelatable
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

    /// <summary>Ambient correlation id stamped on insert by the EF interceptor (spec 13 §4.1). Nullable (fail-open).</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Causation id lineage stamped on insert by the EF interceptor (spec 13 §4.1). Nullable (fail-open).</summary>
    public string? CausationId { get; set; }
}
