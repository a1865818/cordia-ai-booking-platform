using FluentValidation;

namespace Cordia.Application.Handlers.Menu.EditCatalogItems;

/// <summary>Validates <see cref="EditCatalogItemsCommand"/> before the edit transaction opens.</summary>
public sealed class EditCatalogItemsValidator : AbstractValidator<EditCatalogItemsCommand>
{
    public EditCatalogItemsValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.CatalogId).NotEmpty();
        RuleFor(x => x.Actor).NotEmpty();
        RuleFor(x => x.ReasonCode).NotEmpty();
        RuleFor(x => x.Edits).NotEmpty();
        RuleForEach(x => x.Edits).ChildRules(e =>
        {
            e.RuleFor(i => i.ItemId).NotEmpty();
            // A negative price would be approved, published into an immutable version, and served
            // to customers with no later guard (§4.2 price_amount_minor is a money amount).
            e.RuleFor(i => i.PriceMinor).GreaterThanOrEqualTo(0).When(i => i.PriceMinor.HasValue);
        });
    }
}
