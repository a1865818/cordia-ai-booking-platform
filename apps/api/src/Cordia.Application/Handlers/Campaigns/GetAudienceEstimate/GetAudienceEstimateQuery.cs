using Cordia.Application.Handlers.Campaigns;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Campaigns.GetAudienceEstimate;

/// <summary>Returns the stored <c>RecipientsEstimate</c> for a campaign. No live recount.</summary>
public sealed record GetAudienceEstimateQuery(string TenantId, string CampaignId)
    : IRequest<AudienceEstimateResult>;

public sealed class GetAudienceEstimateValidator : AbstractValidator<GetAudienceEstimateQuery>
{
    public GetAudienceEstimateValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.CampaignId).NotEmpty();
    }
}

public sealed class GetAudienceEstimateQueryHandler(ICampaignRepository campaigns)
    : IRequestHandler<GetAudienceEstimateQuery, AudienceEstimateResult>
{
    public async Task<AudienceEstimateResult> Handle(GetAudienceEstimateQuery request, CancellationToken ct)
    {
        var campaign = CampaignProjection.RequireFound(
            await campaigns.GetAsync(request.TenantId, request.CampaignId, ct),
            request.CampaignId);

        return new AudienceEstimateResult(campaign.RecipientsEstimate);
    }
}
