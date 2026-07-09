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
        RuleFor(x => x.ReasonCode).NotEmpty();
        // Edits may be EMPTY (review can just transition draft_ready → in_review), but not null — the
        // handler dereferences command.Edits.Count. (Sibling EditCatalogItems requires NotEmpty because
        // an edit with no edits is meaningless; a review with none is valid.)
        RuleFor(x => x.Edits).NotNull();
        RuleForEach(x => x.Edits).ChildRules(e =>
        {
            e.RuleFor(i => i.ItemId).NotEmpty();
            // Same money guard as EditCatalogItemsValidator: never let a negative price into a
            // version that publish would freeze and serve to customers.
            e.RuleFor(i => i.PriceMinor).GreaterThanOrEqualTo(0).When(i => i.PriceMinor.HasValue);
        });
    }
}
