using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>Operator-written notes and tags for a customer. PK (tenant_id, customer_ref).</summary>
public class CustomerOperatorNotes : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string CustomerRef { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;

    /// <summary>jsonb string array of tags.</summary>
    public string TagsJson { get; set; } = "[]";

    public DateTimeOffset UpdatedAt { get; set; }
}
