using Cordia.Domain.Interfaces;

namespace Cordia.Infrastructure.Tenancy;

/// <summary>Scoped, settable implementation of <see cref="ITenantContext"/>.</summary>
public sealed class TenantContext : ITenantContext
{
    public string TenantId { get; set; } = string.Empty;
}
