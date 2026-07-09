using FluentValidation;

namespace Cordia.Application.Handlers.Menu.ApproveCatalog;

/// <summary>Validates <see cref="ApproveCatalogCommand"/> before the approve transaction opens.</summary>
public sealed class ApproveCatalogValidator : AbstractValidator<ApproveCatalogCommand>
{
    public ApproveCatalogValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.CatalogId).NotEmpty();
        RuleFor(x => x.Actor).NotEmpty();
        RuleFor(x => x.ReasonCode).NotEmpty();
    }
}
