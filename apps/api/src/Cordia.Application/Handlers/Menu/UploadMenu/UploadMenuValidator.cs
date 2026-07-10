using FluentValidation;

namespace Cordia.Application.Handlers.Menu.UploadMenu;

/// <summary>Validates <see cref="UploadMenuCommand"/> before the upload transaction opens.</summary>
public sealed class UploadMenuValidator : AbstractValidator<UploadMenuCommand>
{
    public UploadMenuValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Actor).NotEmpty();
        RuleFor(x => x.SourceArtifactRef).NotEmpty();
        RuleFor(x => x.VersionLabel).NotEmpty();
        RuleFor(x => x.ReasonCode).NotEmpty();
        RuleFor(x => x.ActorRole).NotEmpty();
    }
}
