using FluentValidation;

namespace Cordia.Application.Handlers.TenantResolution.ResolveTenantContext;

/// <summary>
/// Structural validation for <see cref="ResolveTenantContextCommand"/> (spec-15 §7). The MediatR
/// ValidationPipelineBehavior is the sole required-field enforcement, so every required field needs
/// an explicit rule. Rejects a value whose channel/signal type is not in the closed enum set and an
/// empty signal value (spec-15 §5.4). Failures → HTTP 400.
/// </summary>
public sealed class ResolveTenantContextValidator : AbstractValidator<ResolveTenantContextCommand>
{
    public ResolveTenantContextValidator()
    {
        RuleFor(x => x.Channel).IsInEnum();
        RuleFor(x => x.SignalType).IsInEnum();
        RuleFor(x => x.SignalValue).NotEmpty();
    }
}
