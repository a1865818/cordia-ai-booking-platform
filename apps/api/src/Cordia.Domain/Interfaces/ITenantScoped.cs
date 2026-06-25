namespace Cordia.Domain.Interfaces;

/// <summary>Marker interface for entities that are scoped to a tenant.</summary>
public interface ITenantScoped
{
    string TenantId { get; }
}
