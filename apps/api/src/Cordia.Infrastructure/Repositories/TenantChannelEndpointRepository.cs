using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="ITenantChannelEndpointRepository"/>.</summary>
public class TenantChannelEndpointRepository(ApplicationDbContext db) : ITenantChannelEndpointRepository
{
    public async Task AddAsync(TenantChannelEndpoint endpoint, CancellationToken ct = default)
    {
        db.TenantChannelEndpoints.Add(endpoint);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Resolves the active endpoint globally (no tenant filter — entity is NOT ITenantScoped).
    /// IgnoreQueryFilters is applied defensively in case a future refactor adds a filter.
    /// </summary>
    public Task<TenantChannelEndpoint?> ResolveActiveAsync(
        TenantChannel channel,
        SignalType signalType,
        string signalValue,
        CancellationToken ct = default)
        => db.TenantChannelEndpoints
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                e => e.Channel == channel
                     && e.SignalType == signalType
                     && e.SignalValue == signalValue
                     && e.Status == EndpointStatus.Active,
                ct);
}
