using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.Campaigns;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Campaigns.CreateCampaign;

/// <summary>
/// Owner creates an outbound campaign. Status is always <see cref="CampaignStatus.Awaiting"/> —
/// nothing is scheduled, let alone sent, without a later owner approve + schedule (g4).
/// </summary>
public sealed record CreateCampaignCommand(
    string TenantId,
    string Title,
    string Audience,
    string Channel,
    string Message,
    int? Recipients,
    string Actor,
    string ReasonCode,
    string ActorRole) : IRequest<CampaignDto>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => Actor;

    public string RequiredRole => StaffRole.Owner;

    public string ActionName => "campaign.created";

    public string TargetType => "campaign";

    public string TargetId => Title;
}

public sealed class CreateCampaignValidator : AbstractValidator<CreateCampaignCommand>
{
    public CreateCampaignValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        // Deliberately NO rule on Actor/ActorRole/ReasonCode — missing identity is a 403 from
        // OperatorActionBehavior, not a 400 (see SetTenantDisplayNameValidator).
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Audience).NotEmpty();
        RuleFor(x => x.Message).NotEmpty();
        RuleFor(x => x.Channel)
            .NotEmpty()
            .Must(c => CampaignChannel.TryParse(c, out _))
            .WithMessage("channel must be one of: web, sms, email, voice, messaging.");
        RuleFor(x => x.Recipients)
            .GreaterThanOrEqualTo(0)
            .When(x => x.Recipients.HasValue);
    }
}

public sealed class CreateCampaignCommandHandler(
    ICampaignRepository campaigns,
    IAuditWriter audit,
    IUnitOfWork uow)
    : IRequestHandler<CreateCampaignCommand, CampaignDto>
{
    public async Task<CampaignDto> Handle(CreateCampaignCommand request, CancellationToken ct)
    {
        CampaignChannel.TryParse(request.Channel, out var channel);
        var now = DateTimeOffset.UtcNow;
        var campaign = new Campaign
        {
            TenantId = request.TenantId,
            CampaignId = $"cp_{Guid.NewGuid():N}",
            Title = request.Title.Trim(),
            Status = CampaignStatus.Awaiting,
            AudienceDescription = request.Audience.Trim(),
            Channel = channel,
            RecipientsEstimate = request.Recipients ?? 0,
            Message = request.Message,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            await campaigns.AddAsync(campaign, ct2);

            await audit.WriteAsync(
                new AuditRecord(
                    TenantId: request.TenantId,
                    ActorType: StaffRole.IsKnown(request.ActorRole) ? request.ActorRole : "owner",
                    ActorId: string.IsNullOrWhiteSpace(request.Actor) ? "unknown" : request.Actor,
                    Action: "campaign.created",
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
