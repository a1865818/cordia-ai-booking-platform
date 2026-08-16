using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Access to outbound campaigns.</summary>
public interface ICampaignRepository
{
    Task<IReadOnlyList<Campaign>> ListAsync(string tenantId, CancellationToken ct = default);

    Task<Campaign?> GetAsync(string tenantId, string campaignId, CancellationToken ct = default);

    Task AddAsync(Campaign campaign, CancellationToken ct = default);

    Task UpdateAsync(Campaign campaign, CancellationToken ct = default);
}
