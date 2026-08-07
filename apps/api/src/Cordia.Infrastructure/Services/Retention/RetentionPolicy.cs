using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Infrastructure.Services.Retention;

/// <summary>
/// Pure, stateless policy helper for idempotency-record retention decisions (spec 12 §6.3, Inv-14).
/// No DB access — unit-testable in isolation.
/// </summary>
/// <remarks>
/// This sweeper operates on the stored <c>retention_deadline</c> column in
/// <c>idempotency_records</c> and the open-work/dispute guard derived from the
/// linked <c>booking_operations.status</c>.  The concrete deadline formula
/// (booking-life + dispute/audit window) and jurisdiction retention for transcripts
/// and verification grants remain open decisions (spec 12 §13); this helper is
/// intentionally extensible to those tables once those decisions land.
/// </remarks>
public static class RetentionPolicy
{
    /// <summary>
    /// Statuses whose rows are safe to physically prune: no open work, no dispute window (Inv-14).
    ///
    /// <para><b>"Prunable", not "terminal" — the rename is the point.</b> This set used to be called
    /// <c>TerminalStatuses</c> and excluded <see cref="OperationStatus.Waitlisted"/>, which reads as
    /// drift from <see cref="OperationStateMachine.TerminalStates"/> and invites someone to "fix" it
    /// by adding the missing member. It is not drift: a waitlisted operation IS terminal to spec 06
    /// and still has a LIVE enrollment that can be matched and offered later, so its data must
    /// survive. Two different questions were wearing one word.</para>
    ///
    /// <para>Now derived from the spec-06 set minus that one documented exclusion, so the shared
    /// half cannot drift while the difference stays explicit and explained.</para>
    ///
    /// <para>An ARRAY because callers building EF queries need it: <c>Contains</c> on an array
    /// translates to SQL <c>= ANY</c>, so the filter runs in the database instead of after every
    /// candidate row has been dragged into memory. <see cref="Prunable"/> keeps the O(1) membership
    /// test for in-memory callers.</para>
    /// </summary>
    public static readonly OperationStatus[] PrunableStatuses =
        [.. OperationStateMachine.TerminalStates.Where(s => s != OperationStatus.Waitlisted)];

    private static readonly HashSet<OperationStatus> Prunable = [.. PrunableStatuses];

    /// <summary>
    /// Returns <c>true</c> when the record is eligible for physical deletion:
    /// the retention window has elapsed AND there is no open work/dispute window.
    /// </summary>
    /// <param name="retentionDeadline">The stored deadline from the record.</param>
    /// <param name="now">The reference "now" clock value (injected for testability).</param>
    /// <param name="linkedStatus">
    /// Status of the linked <c>booking_operations</c> row, or <c>null</c> if no linked
    /// operation exists (orphan) — orphans are prunable once past their deadline.
    /// </param>
    public static bool ShouldPrune(
        DateTimeOffset retentionDeadline,
        DateTimeOffset now,
        OperationStatus? linkedStatus)
        => retentionDeadline < now
           && (linkedStatus is null || Prunable.Contains(linkedStatus.Value));

    /// <summary>
    /// Returns <c>true</c> when <paramref name="status"/>'s rows may be physically pruned.
    ///
    /// <para>Deliberately NOT named <c>IsTerminal</c>: <see cref="OperationStateMachine.IsTerminal"/>
    /// answers a different question and gives a different answer for
    /// <see cref="OperationStatus.Waitlisted"/>. See <see cref="PrunableStatuses"/>.</para>
    /// </summary>
    public static bool IsPrunable(OperationStatus status) => Prunable.Contains(status);

    // ── Spec 14 retention windows (Stage-3 gate decision 2026-07-09) ──────────────
    // Deployment-configurable defaults; jurisdiction-specific values remain an open
    // decision (spec 12 §13 / spec 14 §12). audit_events is NEVER pruned.

    /// <summary>Default window after session end before customer_ref anonymization (90 days).</summary>
    public static readonly TimeSpan DefaultSessionAnonymizationWindow = TimeSpan.FromDays(90);

    /// <summary>Default window after grant expiry/consumption before physical pruning (30 days).</summary>
    public static readonly TimeSpan DefaultGrantPruneWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// A session is anonymized (customer_ref tombstoned, spec 14 §24.3 — anonymize,
    /// never hard-delete) once it has ENDED and the window has elapsed. Live
    /// sessions (ended_at null) are never touched.
    /// </summary>
    public static bool ShouldAnonymizeSession(
        DateTimeOffset? endedAt,
        DateTimeOffset now,
        TimeSpan window)
        => endedAt is not null && endedAt.Value + window < now;

    /// <summary>
    /// A verification grant is prunable once it is consumed or expired AND the
    /// window has elapsed. Active (unexpired, unconsumed) grants are never
    /// eligible — a future expiry keeps <c>expiresAt + window</c> in the future.
    /// </summary>
    public static bool ShouldPruneGrant(
        DateTimeOffset expiresAt,
        DateTimeOffset? consumedAt,
        DateTimeOffset now,
        TimeSpan window)
        => (consumedAt is not null && consumedAt.Value + window < now)
           || expiresAt + window < now;
}
