using Cordia.Application.Handlers.Campaigns;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Campaigns.ListCampaigns;

/// <summary>
/// Tenant-scoped campaign directory. Includes every status — awaiting is first-class, not a
/// client-side flag (G3 / locked guardrail g4).
/// </summary>
public sealed record ListCampaignsQuery(string TenantId) : IRequest<CampaignListResult>;

public sealed class ListCampaignsValidator : AbstractValidator<ListCampaignsQuery>
{
    public ListCampaignsValidator() => RuleFor(x => x.TenantId).NotEmpty();
}

public sealed class ListCampaignsQueryHandler(ICampaignRepository campaigns)
    : IRequestHandler<ListCampaignsQuery, CampaignListResult>
{
    public async Task<CampaignListResult> Handle(ListCampaignsQuery request, CancellationToken ct)
    {
        var rows = await campaigns.ListAsync(request.TenantId, ct);
        return new CampaignListResult([.. rows.Select(CampaignProjection.ToDto)]);
    }
}
