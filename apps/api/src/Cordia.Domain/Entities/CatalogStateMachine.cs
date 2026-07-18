using System.Collections.Frozen;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;

namespace Cordia.Domain.Entities;

/// <summary>
/// Encodes the §5.2 allowed-edge set for the vendor-catalog lifecycle state machine
/// (spec 11 §5.2). This is the single authoritative source of legal catalog transitions in C#.
///
/// <para>Note: the canonical <c>catalog_status</c> pg-enum (frozen decision D4) has exactly
/// nine values and no <c>extract_failed</c> state, so the §5.2 diagram's <c>ExtractFailed</c>
/// terminal is intentionally NOT modelled here (D4 overrides the diagram). A failed extraction
/// leaves the catalog in <see cref="CatalogStatus.Extracting"/> — it never reaches
/// <c>DraftReady</c>/<c>Published</c> and is surfaced via the DLQ (Inv-14); see the ingestion
/// handler.</para>
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
            }.ToFrozenSet(),
            [CatalogStatus.Extracting] = new HashSet<CatalogStatus>
            {
                CatalogStatus.DraftReady,
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
            // Quarantined, Superseded
        }.ToFrozenDictionary();

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
