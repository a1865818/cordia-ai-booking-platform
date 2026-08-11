using FluentValidation;

namespace Cordia.Application.Handlers.Waitlist.Decline;

/// <summary>
/// Structural validation for <see cref="DeclineWaitlistOfferCommand"/> (HTTP-driven; failures → 400).
/// Validators are the sole source of required-field enforcement (docs/dotnet-api-patterns.md).
/// </summary>
public sealed class DeclineWaitlistOfferValidator : AbstractValidator<DeclineWaitlistOfferCommand>
{
    public DeclineWaitlistOfferValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.OfferId).NotEmpty();
        // No rule on Actor — see AcceptWaitlistOfferValidator. A missing actor on an operator
        // action must reach OperatorActionBehavior so the refusal is deny-audited and answered
        // 403, rather than being short-circuited into an unaudited 400 by validation.
    }
}
