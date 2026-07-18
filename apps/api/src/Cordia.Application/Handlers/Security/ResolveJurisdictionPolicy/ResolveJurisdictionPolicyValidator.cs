using FluentValidation;

namespace Cordia.Application.Handlers.Security.ResolveJurisdictionPolicy;

/// <summary>Required-field + closed-channel-set enforcement (sole enforcement point).</summary>
public sealed class ResolveJurisdictionPolicyValidator : AbstractValidator<ResolveJurisdictionPolicyQuery>
{
    public ResolveJurisdictionPolicyValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Channel)
            .NotEmpty()
            .Must(c => c is not null && ResolveJurisdictionPolicyQueryHandler.ChannelMap.ContainsKey(c))
            .WithMessage("Channel must be one of: voice, sms, messaging, web, email.");
    }
}
