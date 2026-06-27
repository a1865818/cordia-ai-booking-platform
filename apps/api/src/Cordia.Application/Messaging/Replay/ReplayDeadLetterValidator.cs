using FluentValidation;

namespace Cordia.Application.Messaging.Replay;

/// <summary>Structural validator for <see cref="ReplayDeadLetterCommand"/> (T7).</summary>
public sealed class ReplayDeadLetterValidator : AbstractValidator<ReplayDeadLetterCommand>
{
    public ReplayDeadLetterValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.MessageId).NotEmpty();
        RuleFor(x => x.ConsumerName).NotEmpty();
    }
}
