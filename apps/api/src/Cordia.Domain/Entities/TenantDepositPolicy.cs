using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>Tenant deposit policy. PK tenant_id. One row per tenant.</summary>
public class TenantDepositPolicy : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public bool Require { get; set; }

    /// <summary><c>per_service</c> or <c>flat</c>.</summary>
    public string AmountPolicy { get; set; } = string.Empty;

    public bool NoShowOnly { get; set; }

    /// <summary>Human window such as <c>24 hours</c>.</summary>
    public string RefundWindow { get; set; } = string.Empty;

    /// <summary>Flat amount in minor units when <see cref="AmountPolicy"/> is <c>flat</c>.</summary>
    public long? AmountMinor { get; set; }

    public string? Currency { get; set; } = "AUD";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
