using FluentValidation;

namespace Cordia.Application.Handlers.Menu.IngestVendorMenu;

/// <summary>Validates <see cref="IngestVendorMenuCommand"/> before the inbox transaction opens.</summary>
public sealed class IngestVendorMenuValidator : AbstractValidator<IngestVendorMenuCommand>
{
    public IngestVendorMenuValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.CatalogId).NotEmpty();
        RuleFor(x => x.SourceArtifactRef).NotEmpty();
        RuleFor(x => x.MessageId).NotEmpty();
    }
}
