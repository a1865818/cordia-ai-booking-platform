using FluentValidation;

namespace Cordia.Application.Handlers.ConversationSessions.CreateConversationSession;

/// <summary>
/// Structural validation for <see cref="CreateConversationSessionCommand"/> (spec-15 D12). The
/// MediatR ValidationPipelineBehavior is the sole required-field enforcement, so every required
/// field needs an explicit rule. Failures → HTTP 400.
/// </summary>
public sealed class CreateConversationSessionValidator : AbstractValidator<CreateConversationSessionCommand>
{
    public CreateConversationSessionValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.SessionId).NotEmpty();
        RuleFor(x => x.ChannelEndpointId).NotEmpty();
        RuleFor(x => x.CorrelationId).NotEmpty();
    }
}
