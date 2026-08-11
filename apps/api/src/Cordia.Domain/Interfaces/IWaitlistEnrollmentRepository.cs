using Cordia.Domain.Entities;
using Cordia.Domain.Waitlist;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Repository for the <see cref="WaitlistEnrollment"/> aggregate (spec 11 §4.4, §5.1).
/// Tenant scope is enforced by the <c>ApplicationDbContext</c> global query filter (Inv-13)
/// except on <see cref="CancelAsync"/>, whose key predicate is tenant-scoped explicitly.
/// </summary>
public interface IWaitlistEnrollmentRepository
{
    /// <summary>Inserts a new enrollment. Flushes within the ambient transaction.</summary>
    Task AddAsync(WaitlistEnrollment enrollment, CancellationToken ct = default);

    /// <summary>
    /// Loads an enrollment by id (cancelled or not), tenant-scoped via the global query filter,
    /// or <c>null</c> when absent.
    /// </summary>
    Task<WaitlistEnrollment?> GetAsync(string enrollmentId, CancellationToken ct = default);
    /// <summary>
    /// Distinct <c>location_id</c> values this tenant has actually enrolled against, ascending.
    ///
    /// The matcher compares locations by exact string (<c>e.LocationId == criteria.LocationId</c>),
    /// so a typo produces an enrolment that can never be matched and reports no error anywhere —
    /// docs §12 gap 7. There is no locations registry to validate against, and inventing one is
    /// spec 12's call, so this surfaces the only un-invented source available: the values already
    /// in use. Enough to catch a typo, and honest about being a usage list rather than a registry.
    /// </summary>
    /// <param name="tenantId">
    /// AUTHORITATIVE, not merely ambient. Every other read added alongside this one takes an
    /// explicit tenant and re-asserts it, because leaning on the global query filter means a
    /// caller in a background scope with an unset <c>ITenantContext</c> either throws or — worse —
    /// returns another venue's rows. These are real location handles for a real venue (Inv-13),
    /// and the doc comment above says "this tenant's" while the old signature had no way to say
    /// which tenant that was.
    /// </param>
    Task<IReadOnlyList<string>> ListLocationsInUseAsync(string tenantId, CancellationToken ct = default);


    /// <summary>
    /// Version-token conditional cancel (§22.1, Inv-6): stamps <paramref name="cancelledAt"/> only
    /// when the row is at <paramref name="expectedVersion"/> AND not already cancelled. Returns
    /// rows-affected (1 = cancelled, 0 = lost race / already cancelled → caller reloads). Cancel is
    /// terminal — a cancelled enrollment is never matched again.
    /// </summary>
    Task<int> CancelAsync(
        string tenantId,
        string enrollmentId,
        long expectedVersion,
        DateTimeOffset cancelledAt,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the enrollments eligible for an opening described by <paramref name="criteria"/>,
    /// ordered FIFO by <c>CreatedAt</c> (spec §5.1, §12 default prioritization). Eligible means:
    /// same location + service, party size ≤ available seats, the slot time within
    /// <c>[AcceptableFrom, AcceptableTo]</c>, not cancelled, and not already holding a live offer
    /// (status Pending / Matched / Offered — Inv-1). Tenant-scoped via the global query filter.
    /// </summary>
    Task<IReadOnlyList<WaitlistEnrollment>> FindEligibleAsync(
        WaitlistMatchCriteria criteria, CancellationToken ct = default);
}
