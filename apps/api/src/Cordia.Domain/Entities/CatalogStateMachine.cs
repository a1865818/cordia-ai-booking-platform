using System.Collections.Frozen;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;

namespace Cordia.Domain.Entities;

/// <summary>
/// Encodes the §5.2 allowed-edge set for the vendor-catalog lifecycle state machine
/// (spec 11 §5.2). This is the single authoritative source of legal catalog transitions in C#.
///
/// <para>The §5.2 diagram's <c>ExtractFailed</c> terminal IS modelled here as of the D4
/// amendment (owner ruling 2026-08-06). Before it, a stalled extraction stayed in
/// <see cref="CatalogStatus.Extracting"/> forever: it never reached <c>DraftReady</c>, and
/// "genuinely failed" was indistinguishable from "still working" in the only field that
/// records catalog state.</para>
/// </summary>
public static class CatalogStateMachine
{
    /// <summary>
    /// Every legal directed edge in the §5.2 state machine, keyed by source state.
    /// States absent from this dictionary have no outgoing edges (terminals:
    /// <see cref="CatalogStatus.Quarantined"/>, <see cref="CatalogStatus.Superseded"/>).
    /// Backed by <see cref="FrozenDictionary"/> / <see cref="FrozenSet"/> for true immutability.
    /// </summary>
    public static readonly IReadOnlyDictionary<CatalogStatus, IReadOnlySet<CatalogStatus>> AllowedTransitions =
        new Dictionary<CatalogStatus, IReadOnlySet<CatalogStatus>>
        {
            [CatalogStatus.Uploaded] = new HashSet<CatalogStatus>
            {
                CatalogStatus.Scanning,
            }.ToFrozenSet(),
            [CatalogStatus.Scanning] = new HashSet<CatalogStatus>
            {
                CatalogStatus.Extracting,
                CatalogStatus.Quarantined,
                // A scan that never returns is as stuck as an extraction that never returns.
                CatalogStatus.ExtractFailed,
            }.ToFrozenSet(),
            [CatalogStatus.Extracting] = new HashSet<CatalogStatus>
            {
                CatalogStatus.DraftReady,
                CatalogStatus.ExtractFailed,
            }.ToFrozenSet(),
            [CatalogStatus.DraftReady] = new HashSet<CatalogStatus>
            {
                CatalogStatus.InReview,
            }.ToFrozenSet(),
            [CatalogStatus.InReview] = new HashSet<CatalogStatus>
            {
                CatalogStatus.Approved,
            }.ToFrozenSet(),
            [CatalogStatus.Approved] = new HashSet<CatalogStatus>
            {
                CatalogStatus.Published,
            }.ToFrozenSet(),
            [CatalogStatus.Published] = new HashSet<CatalogStatus>
            {
                CatalogStatus.Superseded,
            }.ToFrozenSet(),
            // Terminal states — no outgoing edges; absent from the dictionary.
            // Quarantined, Superseded, ExtractFailed.
            //
            // ExtractFailed is terminal rather than retryable on purpose: re-running extraction
            // in place would mutate a catalog an operator may already have looked at and judged.
            // The recovery path is a fresh upload, which supersedes — the same route every other
            // catalog replacement takes.
        }.ToFrozenDictionary();

    /// <summary>
    /// Terminal catalog states — <b>derived</b> from <see cref="AllowedTransitions"/>, so the set
    /// cannot drift from the edge map that defines it.
    /// </summary>
    public static readonly IReadOnlySet<CatalogStatus> TerminalStates =
        Enum.GetValues<CatalogStatus>()
            .Where(s => !AllowedTransitions.ContainsKey(s))
            .ToFrozenSet();

    /// <summary>
    /// States where work is running in a process that may never report back — an antivirus scan or
    /// an extraction job. The ingestion monitor sweeps exactly these for staleness.
    ///
    /// <para><b>Listed, not derived.</b> "Has an outgoing edge" is not the question: <c>Uploaded</c>,
    /// <c>DraftReady</c>, <c>InReview</c> and <c>Approved</c> are all non-terminal too, and every one
    /// of them is waiting on a PERSON, not on a job. Sweeping those for staleness would time out an
    /// operator who has simply not got to the review yet.</para>
    /// </summary>
    public static readonly IReadOnlySet<CatalogStatus> InProgressStates =
        new HashSet<CatalogStatus>
        {
            CatalogStatus.Scanning,
            CatalogStatus.Extracting,
        }.ToFrozenSet();

    /// <summary>Returns <c>true</c> if <paramref name="status"/> is terminal (no outgoing edges).</summary>
    public static bool IsTerminal(CatalogStatus status)
        => TerminalStates.Contains(status);

    /// <summary>Returns <c>true</c> when an external job is running for this catalog.</summary>
    public static bool IsInProgress(CatalogStatus status)
        => InProgressStates.Contains(status);

    /// <summary>Returns <c>true</c> if <paramref name="to"/> is a legal successor of <paramref name="from"/>.</summary>
    public static bool IsAllowed(CatalogStatus from, CatalogStatus to)
        => AllowedTransitions.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>
    /// Validates that <paramref name="to"/> is a legal successor of <paramref name="from"/>.
    /// Throws <see cref="InvalidCatalogStateTransitionException"/> if the edge is not in the §5.2 edge set.
    /// </summary>
    public static void EnsureAllowed(CatalogStatus from, CatalogStatus to)
    {
        if (!IsAllowed(from, to))
            throw new InvalidCatalogStateTransitionException(from, to);
    }
}
