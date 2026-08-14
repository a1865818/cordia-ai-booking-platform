using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <inheritdoc/>
public sealed class CampaignRepository(ApplicationDbContext db) : ICampaignRepository
{
    public async Task<IReadOnlyList<Campaign>> ListAsync(string tenantId, CancellationToken ct = default)
        => await db.Campaigns
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

    public Task<Campaign?> GetAsync(string tenantId, string campaignId, CancellationToken ct = default)
        => db.Campaigns
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.CampaignId == campaignId, ct);

    public async Task AddAsync(Campaign campaign, CancellationToken ct = default)
    {
        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Campaign campaign, CancellationToken ct = default)
    {
        var existing = await db.Campaigns
            .FirstOrDefaultAsync(c => c.TenantId == campaign.TenantId && c.CampaignId == campaign.CampaignId, ct);
        if (existing is null)
            return;

        existing.Title = campaign.Title;
        existing.Status = campaign.Status;
        existing.AudienceDescription = campaign.AudienceDescription;
        existing.Channel = campaign.Channel;
        existing.RecipientsEstimate = campaign.RecipientsEstimate;
        existing.Message = campaign.Message;
        existing.ScheduledAt = campaign.ScheduledAt;
        existing.SentAt = campaign.SentAt;
        existing.RejectedAt = campaign.RejectedAt;
        existing.ApprovedBy = campaign.ApprovedBy;
        existing.ApprovedAt = campaign.ApprovedAt;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
