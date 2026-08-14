using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.Campaigns;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Campaigns.ApproveCampaign;

/// <summary>
/// Owner approval (g4). Stays <see cref="CampaignStatus.Awaiting"/> — schedule is a separate
/// step and is refused until <c>ApprovedAt</c> is set. Does not send.
/// </summary>
public sealed record ApproveCampaignCommand(
    string TenantId,
    string CampaignId,
    string Actor,
    string ReasonCode,
    string ActorRole) : IRequest<CampaignDto>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => Actor;

    public string RequiredRole => StaffRole.Owner;

    public string ActionName => "campaign.approved";

    public string TargetType => "campaign";

    public string TargetId => CampaignId;
}

public sealed class ApproveCampaignValidator : AbstractValidator<ApproveCampaignCommand>
{
    public ApproveCampaignValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.CampaignId).NotEmpty();
        // No Actor/ActorRole/ReasonCode rules — see SetTenantDisplayNameValidator.
    }
}

public sealed class ApproveCampaignCommandHandler(
    ICampaignRepository campaigns,
    IAuditWriter audit,
    IUnitOfWork uow)
    : IRequestHandler<ApproveCampaignCommand, CampaignDto>
{
    public async Task<CampaignDto> Handle(ApproveCampaignCommand request, CancellationToken ct)
    {
        var campaign = CampaignProjection.RequireFound(
            await campaigns.GetAsync(request.TenantId, request.CampaignId, ct),
            request.CampaignId);

        CampaignProjection.RequireAwaiting(campaign, "approve");

        var now = DateTimeOffset.UtcNow;
        campaign.ApprovedBy = request.Actor;
        campaign.ApprovedAt = now;

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            await campaigns.UpdateAsync(campaign, ct2);

            await audit.WriteAsync(
                new AuditRecord(
                    TenantId: request.TenantId,
                    ActorType: StaffRole.IsKnown(request.ActorRole) ? request.ActorRole : "owner",
                    ActorId: string.IsNullOrWhiteSpace(request.Actor) ? "unknown" : request.Actor,
                    Action: "campaign.approved",
                    TargetType: "campaign",
                    TargetId: campaign.CampaignId,
                    Decision: "allow",
                    DataClasses: ["campaign"],
                    ReasonCode: string.IsNullOrWhiteSpace(request.ReasonCode) ? null : request.ReasonCode,
                    CorrelationId: campaign.CampaignId,
                    OccurredAt: now,
                    Extra: new { approved_by = request.Actor, approved_at = now }),
                ct2);
        }, ct);

        return CampaignProjection.ToDto(campaign);
    }
}
