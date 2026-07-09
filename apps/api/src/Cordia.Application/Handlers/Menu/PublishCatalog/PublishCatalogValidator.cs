using FluentValidation;

namespace Cordia.Application.Handlers.Menu.PublishCatalog;

/// <summary>Validates <see cref="PublishCatalogCommand"/> before the publish transaction opens.</summary>
public sealed class PublishCatalogValidator : AbstractValidator<PublishCatalogCommand>
{
    public PublishCatalogValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.CatalogId).NotEmpty();
        RuleFor(x => x.Actor).NotEmpty();
        RuleFor(x => x.ReasonCode).NotEmpty();
    }
}
