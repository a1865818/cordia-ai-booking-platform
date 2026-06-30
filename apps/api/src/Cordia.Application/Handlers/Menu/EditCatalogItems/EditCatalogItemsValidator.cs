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
        RuleFor(x => x.Edits).NotEmpty();
        RuleForEach(x => x.Edits).ChildRules(e => e.RuleFor(i => i.ItemId).NotEmpty());
    }
}
