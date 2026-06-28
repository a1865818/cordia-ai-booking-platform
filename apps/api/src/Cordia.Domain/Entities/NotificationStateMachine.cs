using System.Collections.Frozen;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;

namespace Cordia.Domain.Entities;

/// <summary>
/// Encodes the §6.1 allowed-edge set for the notification delivery state machine
/// (spec 10 §6.1). This is the single authoritative source of legal transitions in C#.
/// </summary>
public static class NotificationStateMachine
{
    /// <summary>
    /// Every legal directed edge in the §6.1 state machine, keyed by source state.
    /// States absent from this dictionary have no outgoing edges (terminals).
    /// Backed by <see cref="FrozenDictionary"/> / <see cref="FrozenSet"/> for true immutability
    /// (C6 — not downcastable to mutable collections).
    /// </summary>
    public static readonly IReadOnlyDictionary<NotificationState, IReadOnlySet<NotificationState>> AllowedTransitions =
        new Dictionary<NotificationState, IReadOnlySet<NotificationState>>
        {
            [NotificationState.Pending] = new HashSet<NotificationState>
            {
                NotificationState.SuppressedDuplicate,
                NotificationState.InvalidRecipient,
                NotificationState.Sending,
            }.ToFrozenSet(),
            [NotificationState.Sending] = new HashSet<NotificationState>
            {
                NotificationState.Accepted,
                NotificationState.FailedTransient,
                NotificationState.FailedPermanent,
                NotificationState.InvalidRecipient,
            }.ToFrozenSet(),
            [NotificationState.FailedTransient] = new HashSet<NotificationState>
            {
                NotificationState.Sending,
                NotificationState.FailedPermanent,
            }.ToFrozenSet(),
            [NotificationState.Accepted] = new HashSet<NotificationState>
            {
                NotificationState.Delivered,
                NotificationState.FailedPermanent,
            }.ToFrozenSet(),
            // Terminal states — no outgoing edges; absent from the dictionary.
            // SuppressedDuplicate, InvalidRecipient, Delivered, FailedPermanent
        }.ToFrozenDictionary();

    /// <summary>Returns <c>true</c> if <paramref name="to"/> is a legal successor of <paramref name="from"/>.</summary>
    public static bool IsAllowed(NotificationState from, NotificationState to)
        => AllowedTransitions.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>
    /// Validates that <paramref name="to"/> is a legal successor of <paramref name="from"/>.
    /// Throws <see cref="InvalidNotificationStateTransitionException"/> if the edge is not in the §6.1 edge set.
    /// </summary>
    public static void EnsureAllowed(NotificationState from, NotificationState to)
    {
        if (!IsAllowed(from, to))
            throw new InvalidNotificationStateTransitionException(from, to);
    }
}
