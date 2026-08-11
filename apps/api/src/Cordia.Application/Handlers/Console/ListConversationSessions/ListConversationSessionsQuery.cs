using Cordia.Domain.Console;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Console.ListConversationSessions;

/// <summary>
/// Conversation sessions for the console's thread list.
///
/// Returns session metadata only — never transcript turns, which stay behind
/// <c>GET /api/conversation-sessions/{id}/transcript</c> with its grant and reason-code
/// requirements (spec 14 §4.2).
/// </summary>
public sealed record ListConversationSessionsQuery(
    string TenantId,
    int Limit) : IRequest<IReadOnlyList<ConversationSessionSummary>>;

public sealed class ListConversationSessionsValidator : AbstractValidator<ListConversationSessionsQuery>
{
    public const int MaxLimit = 200;

    public ListConversationSessionsValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();

        RuleFor(x => x.Limit)
            .GreaterThan(0)
            .LessThanOrEqualTo(MaxLimit)
            .WithMessage($"limit must be between 1 and {MaxLimit}.");
    }
}

public sealed class ListConversationSessionsQueryHandler(IConsoleReadModel reads)
    : IRequestHandler<ListConversationSessionsQuery, IReadOnlyList<ConversationSessionSummary>>
{
    public Task<IReadOnlyList<ConversationSessionSummary>> Handle(
        ListConversationSessionsQuery request,
        CancellationToken ct)
        => reads.ListSessionsAsync(request.TenantId, request.Limit, ct);
}
