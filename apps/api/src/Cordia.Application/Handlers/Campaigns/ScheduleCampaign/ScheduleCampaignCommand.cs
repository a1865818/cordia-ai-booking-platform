using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.Campaigns;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Campaigns.ScheduleCampaign;

/// <summary>
/// Owner records <c>send_at</c> only. Status becomes <see cref="CampaignStatus.Scheduled"/> after
/// approve; refused with 409 when <c>ApprovedAt</c> is unset. Does not blast.
/// </summary>
public sealed record ScheduleCampaignCommand(
    string TenantId,
    string CampaignId,
    DateTimeOffset? SendAt,
    string Actor,
    string ReasonCode,
    string ActorRole) : IRequest<CampaignDto>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => Actor;

    public string RequiredRole => StaffRole.Owner;

    public string ActionName => "campaign.scheduled";

    public string TargetType => "campaign";

    public string TargetId => CampaignId;
}

public sealed class ScheduleCampaignValidator : AbstractValidator<ScheduleCampaignCommand>
{
    public ScheduleCampaignValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.CampaignId).NotEmpty();
        RuleFor(x => x.SendAt)
            .NotNull()
            .WithMessage("send_at is required.");
    }
}

public sealed class ScheduleCampaignCommandHandler(
    ICampaignRepository campaigns,
    IAuditWriter audit,
    IUnitOfWork uow)
    : IRequestHandler<ScheduleCampaignCommand, CampaignDto>
{
    public async Task<CampaignDto> Handle(ScheduleCampaignCommand request, CancellationToken ct)
    {
        var campaign = CampaignProjection.RequireFound(
            await campaigns.GetAsync(request.TenantId, request.CampaignId, ct),
            request.CampaignId);

        CampaignProjection.RequireAwaiting(campaign, "schedule");

        if (campaign.ApprovedAt is null)
        {
            throw new OperationConflictException(
                campaign.CampaignId,
                "Campaign must be approved before it can be scheduled.");
        }

        var now = DateTimeOffset.UtcNow;
        campaign.Status = CampaignStatus.Scheduled;
        campaign.ScheduledAt = request.SendAt;

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            await campaigns.UpdateAsync(campaign, ct2);

            await audit.WriteAsync(
                new AuditRecord(
                    TenantId: request.TenantId,
                    ActorType: StaffRole.IsKnown(request.ActorRole) ? request.ActorRole : "owner",
                    ActorId: string.IsNullOrWhiteSpace(request.Actor) ? "unknown" : request.Actor,
                    Action: "campaign.scheduled",
                    TargetType: "campaign",
                    TargetId: campaign.CampaignId,
                    Decision: "allow",
                    DataClasses: ["campaign"],
                    ReasonCode: string.IsNullOrWhiteSpace(request.ReasonCode) ? null : request.ReasonCode,
                    CorrelationId: campaign.CampaignId,
                    OccurredAt: now,
                    Extra: new { send_at = request.SendAt }),
                ct2);
        }, ct);

        return CampaignProjection.ToDto(campaign);
    }
}
