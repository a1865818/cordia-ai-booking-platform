using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>
/// Server-issued, expiring, action-scoped authorization grant (spec 08 / spec 12 §5.5 DDL).
/// PK (tenant_id, grant_id). No FK — customer_ref and target_booking_id are loose refs.
/// No version token — no optimistic-concurrency needed (consumed_at written once).
/// </summary>
public class VerificationGrant : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string GrantId { get; set; } = string.Empty;

    public string CustomerRef { get; set; } = string.Empty;

    public string? TargetBookingId { get; set; }

    public VerificationActionEnum AllowedAction { get; set; }

    public VerificationLevel VerificationLevel { get; set; }

    public string? SessionOrOperationRef { get; set; }

    /// <summary>Defaults to now() in the database.</summary>
    public DateTimeOffset IssuedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }
}
