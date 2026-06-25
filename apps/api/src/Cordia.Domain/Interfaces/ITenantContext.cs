namespace Cordia.Domain.Interfaces;

/// <summary>Provides the current tenant identifier for query filtering.</summary>
public interface ITenantContext
{
    string TenantId { get; }
}
