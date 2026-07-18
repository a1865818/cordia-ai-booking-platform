using FluentValidation;

namespace Cordia.Application.Handlers.Waitlist.Accept;

/// <summary>
/// Structural validation for <see cref="AcceptWaitlistOfferCommand"/> (HTTP-driven; failures → 400).
/// Validators are the sole source of required-field enforcement (docs/dotnet-api-patterns.md).
/// </summary>
public sealed class AcceptWaitlistOfferValidator : AbstractValidator<AcceptWaitlistOfferCommand>
{
    public AcceptWaitlistOfferValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.OfferId).NotEmpty();
        RuleFor(x => x.Actor).NotEmpty();
    }
}
