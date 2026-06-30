using FluentValidation;

namespace Cordia.Application.Handlers.Menu.SearchVendorCatalog;

/// <summary>
/// Validates <see cref="SearchVendorCatalogQuery"/>. Paging is bounded so the foreground
/// tool cannot dump the whole menu into prompt context (spec 11 §7 / §21.2).
/// </summary>
public sealed class SearchVendorCatalogValidator : AbstractValidator<SearchVendorCatalogQuery>
{
    /// <summary>Maximum page size for a single catalog search (token budget, §7).</summary>
    public const int MaxPageSize = 50;

    public SearchVendorCatalogValidator()
    {
        RuleFor(x => x.Skip).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Take).InclusiveBetween(1, MaxPageSize);
    }
}
