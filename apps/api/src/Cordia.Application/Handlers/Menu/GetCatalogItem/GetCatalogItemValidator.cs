using FluentValidation;

namespace Cordia.Application.Handlers.Menu.GetCatalogItem;

/// <summary>Validates <see cref="GetCatalogItemQuery"/>.</summary>
public sealed class GetCatalogItemValidator : AbstractValidator<GetCatalogItemQuery>
{
    public GetCatalogItemValidator()
    {
        RuleFor(x => x.ItemId).NotEmpty();
    }
}
