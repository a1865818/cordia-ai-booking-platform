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
