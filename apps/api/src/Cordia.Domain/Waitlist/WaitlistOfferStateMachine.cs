using System.Collections.Frozen;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;

namespace Cordia.Domain.Waitlist;

/// <summary>
/// Encodes the §4.4 / §5.1 allowed-edge set for the waitlist-offer lifecycle — the single
/// authoritative source of legal offer transitions in C#.
///
/// <para>Lifecycle (spec §4.4): <c>Pending → Matched</c> (opening detected, temporary hold
/// acquired via 07) <c>→ Offered</c> (offer notification enqueued) <c>→ Accepted / Declined /
/// Expired</c>. A held offer can be <c>Released</c> from either <c>Matched</c> (hold acquired,
/// not yet offered) or <c>Offered</c> (offer outstanding). <c>Accepted / Declined / Expired /
/// Released</c> are terminal.</para>
/// </summary>
public static class WaitlistOfferStateMachine
{
    /// <summary>
    /// Every legal directed edge keyed by source state. States absent from this dictionary have
    /// no outgoing edges (terminals: <see cref="WaitlistOfferStatus.Accepted"/>,
    /// <see cref="WaitlistOfferStatus.Declined"/>, <see cref="WaitlistOfferStatus.Expired"/>,
    /// <see cref="WaitlistOfferStatus.Released"/>). Backed by <see cref="FrozenDictionary{TKey,TValue}"/> /
    /// <see cref="FrozenSet{T}"/> for true immutability.
    /// </summary>
    public static readonly IReadOnlyDictionary<WaitlistOfferStatus, IReadOnlySet<WaitlistOfferStatus>> AllowedTransitions =
        new Dictionary<WaitlistOfferStatus, IReadOnlySet<WaitlistOfferStatus>>
        {
            [WaitlistOfferStatus.Pending] = new HashSet<WaitlistOfferStatus>
            {
                WaitlistOfferStatus.Matched,
            }.ToFrozenSet(),
            [WaitlistOfferStatus.Matched] = new HashSet<WaitlistOfferStatus>
            {
                WaitlistOfferStatus.Offered,
                WaitlistOfferStatus.Released,
            }.ToFrozenSet(),
            [WaitlistOfferStatus.Offered] = new HashSet<WaitlistOfferStatus>
            {
                WaitlistOfferStatus.Accepted,
                WaitlistOfferStatus.Declined,
                WaitlistOfferStatus.Expired,
                WaitlistOfferStatus.Released,
            }.ToFrozenSet(),
            // Terminal states — no outgoing edges; absent from the dictionary.
            // Accepted, Declined, Expired, Released
        }.ToFrozenDictionary();

    /// <summary>
    /// Terminal offer states — <b>derived</b> from <see cref="AllowedTransitions"/>, so the set
    /// cannot drift from the edge map that defines it.
    /// </summary>
    public static readonly IReadOnlySet<WaitlistOfferStatus> TerminalStates =
        Enum.GetValues<WaitlistOfferStatus>()
            .Where(s => !AllowedTransitions.ContainsKey(s))
            .ToFrozenSet();

    /// <summary>Returns <c>true</c> if <paramref name="status"/> is terminal (no outgoing edges).</summary>
    public static bool IsTerminal(WaitlistOfferStatus status)
        => TerminalStates.Contains(status);

    /// <summary>Returns <c>true</c> if <paramref name="to"/> is a legal successor of <paramref name="from"/>.</summary>
    public static bool IsAllowed(WaitlistOfferStatus from, WaitlistOfferStatus to)
        => AllowedTransitions.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>
    /// Validates that <paramref name="to"/> is a legal successor of <paramref name="from"/>.
    /// Throws <see cref="InvalidWaitlistOfferTransitionException"/> if the edge is not in the §4.4 edge set.
    /// </summary>
    public static void EnsureAllowed(WaitlistOfferStatus from, WaitlistOfferStatus to)
    {
        if (!IsAllowed(from, to))
            throw new InvalidWaitlistOfferTransitionException(from, to);
    }
}
