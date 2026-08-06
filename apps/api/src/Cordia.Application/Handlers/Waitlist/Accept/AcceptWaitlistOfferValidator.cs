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
        // Deliberately NO rule on Actor. Accept is an operator action, so a missing actor is an
        // AUTHORIZATION failure, not a shape problem — and `ValidationPipelineBehavior` runs
        // BEFORE `OperatorActionBehavior`, so a NotEmpty rule here answered 400 and returned
        // before the behavior could deny-audit the attempt (spec 14 §3.1 step 5). An
        // unauthorized caller was therefore turned away without a record of having tried.
        // Cancel and enrol already omit it for the same reason.
    }
}
