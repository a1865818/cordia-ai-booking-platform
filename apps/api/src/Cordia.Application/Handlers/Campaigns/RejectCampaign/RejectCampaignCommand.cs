using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.Campaigns;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Campaigns.RejectCampaign;

/// <summary>
/// Owner rejection (g4). Terminal: <see cref="CampaignStatus.Rejected"/> cannot transition.
/// </summary>
public sealed record RejectCampaignCommand(
    string TenantId,
    string CampaignId,
    string Actor,
    string ReasonCode,
    string ActorRole) : IRequest<CampaignDto>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => Actor;

    public string RequiredRole => StaffRole.Owner;

    public string ActionName => "campaign.rejected";

    public string TargetType => "campaign";

    public string TargetId => CampaignId;
}

public sealed class RejectCampaignValidator : AbstractValidator<RejectCampaignCommand>
{
    public RejectCampaignValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.CampaignId).NotEmpty();
    }
}

public sealed class RejectCampaignCommandHandler(
    ICampaignRepository campaigns,
    IAuditWriter audit,
    IUnitOfWork uow)
    : IRequestHandler<RejectCampaignCommand, CampaignDto>
{
    public async Task<CampaignDto> Handle(RejectCampaignCommand request, CancellationToken ct)
    {
        var campaign = CampaignProjection.RequireFound(
            await campaigns.GetAsync(request.TenantId, request.CampaignId, ct),
            request.CampaignId);

        CampaignProjection.RequireAwaiting(campaign, "reject");

        var now = DateTimeOffset.UtcNow;
        campaign.Status = CampaignStatus.Rejected;
        campaign.RejectedAt = now;

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            await campaigns.UpdateAsync(campaign, ct2);

            await audit.WriteAsync(
                new AuditRecord(
                    TenantId: request.TenantId,
                    ActorType: StaffRole.IsKnown(request.ActorRole) ? request.ActorRole : "owner",
                    ActorId: string.IsNullOrWhiteSpace(request.Actor) ? "unknown" : request.Actor,
                    Action: "campaign.rejected",
                    TargetType: "campaign",
                    TargetId: campaign.CampaignId,
                    Decision: "allow",
                    DataClasses: ["campaign"],
                    ReasonCode: string.IsNullOrWhiteSpace(request.ReasonCode) ? null : request.ReasonCode,
                    CorrelationId: campaign.CampaignId,
                    OccurredAt: now),
                ct2);
        }, ct);

        return CampaignProjection.ToDto(campaign);
    }
}
