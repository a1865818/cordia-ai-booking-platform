using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>
/// Stripe-backed deposit session. PK (tenant_id, deposit_session_id).
/// FK (tenant_id, operation_id) → booking_operations ON DELETE RESTRICT.
/// Optimistic-concurrency version token (§22.1).
/// </summary>
public class DepositSession : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string DepositSessionId { get; set; } = string.Empty;

    public string OperationId { get; set; } = string.Empty;

    public int AmountMinor { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string? StripeSessionId { get; set; }

    public string? PaymentLinkUrl { get; set; }

    public DepositStatus Status { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? PaidAt { get; set; }

    /// <summary>Optimistic-concurrency token (§22.1); default 0.</summary>
    public long Version { get; set; }
}
