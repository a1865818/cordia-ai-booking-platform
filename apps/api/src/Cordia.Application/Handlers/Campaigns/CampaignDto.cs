using System.Text.Json.Serialization;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;

namespace Cordia.Application.Handlers.Campaigns;

/// <summary>Wire shape for a campaign row (G3). <c>when</c> is a display string, not a timestamp.</summary>
public sealed record CampaignDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("audience")] string Audience,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("recipients")] int Recipients,
    [property: JsonPropertyName("when")] string When,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("approved")] bool Approved);

public sealed record CampaignListResult(
    [property: JsonPropertyName("campaigns")] IReadOnlyList<CampaignDto> Campaigns);

public sealed record AudienceEstimateResult(
    [property: JsonPropertyName("recipients")] int Recipients);

/// <summary>
/// Maps stored <see cref="TenantChannel"/> onto the campaign wire, accepting the console aliases
/// (sms/phone/whatsapp/instagram) on input while persisting the pg enum.
/// </summary>
public static class CampaignChannel
{
    private static readonly Dictionary<string, TenantChannel> Aliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["voice"] = TenantChannel.Voice,
            ["phone"] = TenantChannel.Voice,
            ["sms"] = TenantChannel.Messaging,
            ["messaging"] = TenantChannel.Messaging,
            ["whatsapp"] = TenantChannel.Messaging,
            ["instagram"] = TenantChannel.Messaging,
            ["web"] = TenantChannel.Web,
            ["email"] = TenantChannel.Email,
        };

    public static bool TryParse(string? wire, out TenantChannel channel)
    {
        channel = default;
        return !string.IsNullOrWhiteSpace(wire) && Aliases.TryGetValue(wire.Trim(), out channel);
    }
}

internal static class CampaignProjection
{
    public static CampaignDto ToDto(Campaign campaign) => new(
        campaign.CampaignId,
        campaign.Title,
        EnumWireNames.ToWireName(campaign.Status),
        campaign.AudienceDescription,
        EnumWireNames.ToWireName(campaign.Channel),
        campaign.RecipientsEstimate,
        WhenLabel(campaign),
        campaign.Message,
        campaign.ApprovedAt is not null);

    public static string WhenLabel(Campaign campaign) => campaign.Status switch
    {
        CampaignStatus.Awaiting => "Holding for your approval",
        CampaignStatus.Scheduled => campaign.ScheduledAt?.ToString("O") ?? string.Empty,
        CampaignStatus.Sent => campaign.SentAt is { } sent ? $"Sent {sent:O}" : "Sent …",
        CampaignStatus.Rejected => "Rejected",
        _ => string.Empty,
    };

    public static Campaign RequireFound(Campaign? campaign, string campaignId)
        => campaign ?? throw new DeadLetterNotFoundException($"Campaign '{campaignId}' was not found.");

    public static void RequireAwaiting(Campaign campaign, string action)
    {
        if (campaign.Status != CampaignStatus.Awaiting)
        {
            throw new OperationConflictException(
                campaign.CampaignId,
                $"Campaign cannot {action} from status '{EnumWireNames.ToWireName(campaign.Status)}'.");
        }
    }
}
