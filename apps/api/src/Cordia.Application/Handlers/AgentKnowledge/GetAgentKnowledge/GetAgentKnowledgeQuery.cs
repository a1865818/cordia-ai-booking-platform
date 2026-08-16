using System.Text.Json.Serialization;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.AgentKnowledge;

/// <summary>
/// Lists agent knowledge-base entries (docs/backend-gaps.md G9). Not owner-gated — the
/// console needs the unanswered count on the settings panel.
///
/// TODO: also include open <c>HandoffWorkItem</c> rows that have no knowledge entry yet as
/// <c>needs-answer</c> sourced from <c>handoff</c>. <c>IHandoffWorkItemRepository</c> currently
/// has no list-open-items method (only <c>GetByIdempotencyKeyAsync</c> / <c>TryAddAsync</c>),
/// so auto-seed from handoffs is skipped until that surface exists.
/// </summary>
public sealed record GetAgentKnowledgeQuery(string TenantId) : IRequest<AgentKnowledgeListResult>;

public sealed record AgentKnowledgeEntryResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("q")] string Q,
    [property: JsonPropertyName("a")] string? A,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("source")] string Source);

public sealed record AgentKnowledgeListResult(
    [property: JsonPropertyName("entries")] IReadOnlyList<AgentKnowledgeEntryResult> Entries);

public static class AgentKnowledgeStatusWire
{
    public const string NeedsAnswer = "needs-answer";
    public const string Answered = "answered";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        NeedsAnswer, Answered,
    };

    public static string From(KnowledgeStatus status)
        => status == KnowledgeStatus.Answered ? Answered : NeedsAnswer;

    public static KnowledgeStatus Parse(string status)
        => string.Equals(status, Answered, StringComparison.OrdinalIgnoreCase)
            ? KnowledgeStatus.Answered
            : KnowledgeStatus.NeedsAnswer;

    public static KnowledgeStatus FromAnswer(string? answer)
        => string.IsNullOrWhiteSpace(answer) ? KnowledgeStatus.NeedsAnswer : KnowledgeStatus.Answered;

    public static AgentKnowledgeEntryResult ToResult(Cordia.Domain.Entities.AgentKnowledgeEntry entry)
        => new(entry.EntryId, entry.Question, entry.Answer, From(entry.Status), entry.Source);
}

public sealed class GetAgentKnowledgeQueryHandler(IAgentKnowledgeRepository repo)
    : IRequestHandler<GetAgentKnowledgeQuery, AgentKnowledgeListResult>
{
    public async Task<AgentKnowledgeListResult> Handle(GetAgentKnowledgeQuery request, CancellationToken ct)
    {
        var entries = await repo.ListAsync(request.TenantId, ct);
        return new AgentKnowledgeListResult(entries.Select(AgentKnowledgeStatusWire.ToResult).ToList());
    }
}
