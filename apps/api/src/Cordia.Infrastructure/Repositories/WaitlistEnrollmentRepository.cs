using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Waitlist;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.EntityFramework.Concurrency;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IWaitlistEnrollmentRepository"/> (spec 11 §4.4, §5.1).
/// Tenant scope is enforced by the global query filter (Inv-13); reads use <c>.AsNoTracking()</c>.
/// </summary>
public class WaitlistEnrollmentRepository(ApplicationDbContext db) : IWaitlistEnrollmentRepository
{
    public async Task AddAsync(WaitlistEnrollment enrollment, CancellationToken ct = default)
    {
        db.WaitlistEnrollments.Add(enrollment);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<string>> ListLocationsInUseAsync(
        string tenantId, CancellationToken ct = default)
        // Explicit tenant predicate, matching every sibling read: the global filter is a backstop,
        // not the scope statement (Inv-13).
        => await db.WaitlistEnrollments
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.LocationId != "")
            .Select(e => e.LocationId)
            .Distinct()
            .OrderBy(l => l)
            .ToListAsync(ct);

    public Task<WaitlistEnrollment?> GetAsync(string enrollmentId, CancellationToken ct = default)
        => db.WaitlistEnrollments
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.EnrollmentId == enrollmentId, ct);

    public Task<int> CancelAsync(
        string tenantId,
        string enrollmentId,
        long expectedVersion,
        DateTimeOffset cancelledAt,
        CancellationToken ct = default)
        => VersionTokenUpdate.UpdateAsync<WaitlistEnrollment>(
            db,
            expectedVersion,
            e => e.TenantId == tenantId && e.EnrollmentId == enrollmentId && e.CancelledAt == null,
            s => s.SetProperty(x => x.CancelledAt, cancelledAt),
            ct);

    public async Task<IReadOnlyList<WaitlistEnrollment>> FindEligibleAsync(
        WaitlistMatchCriteria criteria, CancellationToken ct = default)
    {
        // Enrollments already holding a live offer are excluded so at most one offer is live per
        // enrollment at a time (Inv-1).
        var liveEnrollmentIds = db.WaitlistOffers
            .Where(o => o.Status == WaitlistOfferStatus.Pending
                || o.Status == WaitlistOfferStatus.Matched
                || o.Status == WaitlistOfferStatus.Offered)
            .Select(o => o.EnrollmentId);

        // Enrollments that already received an offer for THIS slot (e.g. declined/expired it) are
        // excluded from re-matching the same opening, so re-match advances to the next enrollee
        // rather than looping back to the one who just declined.
        var thisSlotEnrollmentIds = db.WaitlistOffers
            .Where(o => o.SlotId == criteria.SlotId)
            .Select(o => o.EnrollmentId);

        return await db.WaitlistEnrollments
            .AsNoTracking()
            .Where(e => e.CancelledAt == null
                && e.LocationId == criteria.LocationId
                && e.ServiceId == criteria.ServiceId
                && e.PartySize <= criteria.AvailableSeats
                && e.AcceptableFrom <= criteria.SlotStartAt
                && e.AcceptableTo >= criteria.SlotStartAt
                && !liveEnrollmentIds.Contains(e.EnrollmentId)
                && !thisSlotEnrollmentIds.Contains(e.EnrollmentId))
            .OrderBy(e => e.CreatedAt)
            .ThenBy(e => e.EnrollmentId)
            // The matcher offers ONE opening to the FIFO-first enrollee; never hydrate the whole
            // eligible set (it scales with waitlist depth on the per-cancellation hot path).
            .Take(1)
            .ToListAsync(ct);
    }
}
