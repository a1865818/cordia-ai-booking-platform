using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>Outbound campaign. PK (tenant_id, campaign_id). Default status is Awaiting.</summary>
public class Campaign : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string CampaignId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public CampaignStatus Status { get; set; } = CampaignStatus.Awaiting;

    public string AudienceDescription { get; set; } = string.Empty;

    public TenantChannel Channel { get; set; }

    public int RecipientsEstimate { get; set; }

    public string Message { get; set; } = string.Empty;

    public DateTimeOffset? ScheduledAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    public DateTimeOffset? RejectedAt { get; set; }

    public string? ApprovedBy { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
