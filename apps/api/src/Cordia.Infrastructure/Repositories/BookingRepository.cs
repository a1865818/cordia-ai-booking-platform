using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.EntityFramework.Concurrency;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IBookingRepository"/>.</summary>
public class BookingRepository(ApplicationDbContext db) : IBookingRepository
{
    public async Task AddAsync(Booking booking, CancellationToken ct = default)
    {
        db.Bookings.Add(booking);
        await db.SaveChangesAsync(ct);
    }

    public async Task MaterializeBookedAsync(Booking booking, CancellationToken ct = default)
    {
        var existing = await db.Bookings
            .FirstOrDefaultAsync(b => b.TenantId == booking.TenantId && b.BookingId == booking.BookingId, ct);

        if (existing is null)
        {
            db.Bookings.Add(booking);
        }
        else
        {
            existing.BookingReference = booking.BookingReference;
            existing.ProviderReference = booking.ProviderReference;
            existing.LifecycleStatus = BookingLifecycleStatus.Confirmed;
            existing.CustomerId = booking.CustomerId;
            existing.UpdatedAt = booking.UpdatedAt;
        }

        await db.SaveChangesAsync(ct);
    }

    public Task<Booking?> GetByIdAsync(string tenantId, string bookingId, CancellationToken ct = default)
        => db.Bookings
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.TenantId == tenantId && b.BookingId == bookingId, ct);

    public Task<Booking?> GetByReferenceAsync(string tenantId, string bookingReference, CancellationToken ct = default)
        => db.Bookings
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.TenantId == tenantId && b.BookingReference == bookingReference, ct);

    public async Task<IReadOnlyList<Booking>> ListByCustomerAsync(
        string tenantId,
        string customerId,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (limit <= 0)
            return [];

        var results = await db.Bookings
            .AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.CustomerId == customerId)
            .OrderByDescending(b => b.UpdatedAt)
            .Take(limit)
            .ToListAsync(ct);

        return results;
    }

    public Task<long?> GetCurrentVersionAsync(string tenantId, string bookingId, CancellationToken ct = default)
        => db.Bookings
            .AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.BookingId == bookingId)
            .Select(b => (long?)b.Version)
            .FirstOrDefaultAsync(ct);

    public Task<bool> HasExpectedVersionAsync(
        string tenantId,
        string bookingId,
        long expectedVersion,
        CancellationToken ct = default)
        => db.Bookings
            .AsNoTracking()
            .AnyAsync(b => b.TenantId == tenantId && b.BookingId == bookingId && b.Version == expectedVersion, ct);

    public Task<int> TryTransitionLifecycleAsync(
        string tenantId,
        string bookingId,
        long expectedVersion,
        BookingLifecycleStatus next,
        DateTimeOffset updatedAt,
        CancellationToken ct = default)
        => VersionTokenUpdate.UpdateAsync<Booking>(
            db,
            expectedVersion,
            b => b.TenantId == tenantId && b.BookingId == bookingId,
            s => s
                .SetProperty(b => b.LifecycleStatus, next)
                .SetProperty(b => b.UpdatedAt, updatedAt),
            ct);
}
