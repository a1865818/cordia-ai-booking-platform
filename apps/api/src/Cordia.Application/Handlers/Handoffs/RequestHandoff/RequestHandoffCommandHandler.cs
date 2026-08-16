using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.Handoffs.RequestHandoff;

/// <summary>
/// Creates (or returns) the human-handoff work item for a session.
///
/// Two reads of the dedup key, on purpose. The first is the common path: a
/// retry after the agent lost the response finds the existing item without
/// attempting a write. The second happens only when the insert lost a race —
/// the unique index rejected it because a concurrent reconnect got there first —
/// and turns that into the same <c>existing</c> answer rather than an error.
/// The agent's SingleFlight serialises escalations within one process; nothing
/// serialises them across two, which is exactly the reconnect case (Inv-5).
/// </summary>
public sealed class RequestHandoffCommandHandler(IHandoffWorkItemRepository repository)
    : IRequestHandler<RequestHandoffCommand, HandoffResult>
{
    /// <summary>Status for a work item this call created.</summary>
    public const string StatusCreated = "created";

    /// <summary>Status for a work item an earlier call with the same key created.</summary>
    public const string StatusExisting = "existing";

    /// <summary>
    /// The only queue there is today. Routing by reason code / skill / language
    /// belongs to whatever staffs the queue, and inventing a second name here
    /// before anything consumes it would be a fiction the agent would faithfully
    /// report back to the caller.
    /// </summary>
    public const string DefaultQueue = "default";

    /// <summary>Lifecycle state a freshly raised escalation starts in.</summary>
    public const string StateOpen = "open";

    public async Task<HandoffResult> Handle(RequestHandoffCommand command, CancellationToken ct)
    {
        var existing = await repository.GetByIdempotencyKeyAsync(
            command.TenantId, command.HandoffIdempotencyKey, ct);
        if (existing is not null)
            return new HandoffResult(existing.WorkItemId, existing.Queue, StatusExisting);

        var workItem = new HandoffWorkItem
        {
            TenantId = command.TenantId,
            WorkItemId = $"hwi_{Guid.NewGuid():N}",
            SessionId = command.SessionId,
            ReasonCode = command.ReasonCode,
            HandoffIdempotencyKey = command.HandoffIdempotencyKey,
            Queue = DefaultQueue,
            State = StateOpen,
            RecommendedAction = command.RecommendedAction,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        if (await repository.TryAddAsync(workItem, ct))
            return new HandoffResult(workItem.WorkItemId, workItem.Queue, StatusCreated);

        // Lost the insert race: the winner's row is the answer. Re-read rather
        // than reporting our own (never-persisted) id, which would hand the
        // agent a work item id no human queue will ever show.
        var winner = await repository.GetByIdempotencyKeyAsync(
            command.TenantId, command.HandoffIdempotencyKey, ct);

        return winner is not null
            ? new HandoffResult(winner.WorkItemId, winner.Queue, StatusExisting)
            : new HandoffResult(workItem.WorkItemId, workItem.Queue, StatusExisting);
    }
}
