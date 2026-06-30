using FluentValidation;

namespace Cordia.Application.Handlers.Menu.ReviewCatalog;

/// <summary>Validates <see cref="ReviewCatalogCommand"/> before the review transaction opens.</summary>
public sealed class ReviewCatalogValidator : AbstractValidator<ReviewCatalogCommand>
{
    public ReviewCatalogValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.CatalogId).NotEmpty();
        RuleFor(x => x.Actor).NotEmpty();
        RuleForEach(x => x.Edits).ChildRules(e => e.RuleFor(i => i.ItemId).NotEmpty());
    }
}
