using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>Console operator account. PK (tenant_id, operator_id). Unique (tenant_id, email).</summary>
public class Operator : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string OperatorId { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Wire value: <c>owner</c> or <c>operator</c> (matches StaffRole).</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>Wire value: <c>active</c> or <c>disabled</c>.</summary>
    public string Status { get; set; } = "active";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
