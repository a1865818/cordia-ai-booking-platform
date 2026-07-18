using FluentValidation;

namespace Cordia.Application.Handlers.DeadLetters;

/// <summary>Structural validator for <see cref="ReplayDeadLetterCommand"/> (T7).</summary>
public sealed class ReplayDeadLetterValidator : AbstractValidator<ReplayDeadLetterCommand>
{
    public ReplayDeadLetterValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.MessageId).NotEmpty();
        RuleFor(x => x.ConsumerName).NotEmpty();
        // Spec 14 §24.5 — operator actions carry an authenticated-ish actor
        // identity (X-Actor shim), a staff role (X-Actor-Role, GD-1 matrix),
        // and a mandatory reason code, all audited.
        RuleFor(x => x.ActorId).NotEmpty();
        RuleFor(x => x.ReasonCode).NotEmpty();
        RuleFor(x => x.ActorRole).NotEmpty();
    }
}
