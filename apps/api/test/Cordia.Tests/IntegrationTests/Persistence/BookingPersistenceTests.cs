#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class BookingPersistenceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task AddAndReadByReference_PersistsAuthoritativeBooking()
    {
        var tenantId = $"t8-booking-{Guid.NewGuid():N}";
        var booking = MakeBooking(tenantId, "bk-authoritative", "CORD-AUTH", "cust-a");

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var repo = new BookingRepository(ctx);
            await repo.AddAsync(booking);
        }

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var repo = new BookingRepository(ctx);
            var reloaded = await repo.GetByReferenceAsync(tenantId, "CORD-AUTH");

            Assert.NotNull(reloaded);
            Assert.Equal("bk-authoritative", reloaded.BookingId);
            Assert.Equal("provider-bk-authoritative", reloaded.ProviderReference);
            Assert.Equal("cust-a", reloaded.CustomerId);
            Assert.Equal(BookingLifecycleStatus.Confirmed, reloaded.LifecycleStatus);
        }
    }

    [Fact]
    public async Task Queries_AreTenantScoped()
    {
        var tenantA = $"t8-booking-a-{Guid.NewGuid():N}";
        var tenantB = $"t8-booking-b-{Guid.NewGuid():N}";
        const string sameReference = "CORD-SHARED";

        await using (var ctx = fixture.CreateContext(tenantA))
            await new BookingRepository(ctx).AddAsync(MakeBooking(tenantA, "bk-a", sameReference, "cust-shared"));

        await using (var ctx = fixture.CreateContext(tenantB))
            await new BookingRepository(ctx).AddAsync(MakeBooking(tenantB, "bk-b", sameReference, "cust-shared"));

        await using (var ctx = fixture.CreateContext(tenantA))
        {
            var repo = new BookingRepository(ctx);
            var byReference = await repo.GetByReferenceAsync(tenantA, sameReference);
            var byWrongTenant = await repo.GetByIdAsync(tenantB, "bk-b");
            var byCustomer = await repo.ListByCustomerAsync(tenantA, "cust-shared");

            Assert.Equal("bk-a", byReference?.BookingId);
            Assert.Null(byWrongTenant);
            Assert.Collection(byCustomer, b => Assert.Equal("bk-a", b.BookingId));
        }
    }

    [Theory]
    [InlineData(BookingLifecycleStatus.Confirmed)]
    [InlineData(BookingLifecycleStatus.ModificationPending)]
    [InlineData(BookingLifecycleStatus.CancellationPending)]
    [InlineData(BookingLifecycleStatus.Cancelled)]
    public async Task LifecycleStatus_RoundtripsPgEnum(BookingLifecycleStatus status)
    {
        var tenantId = $"t8-booking-enum-{Guid.NewGuid():N}";
        var bookingId = $"bk-{status}-{Guid.NewGuid():N}";
        var booking = MakeBooking(tenantId, bookingId, $"CORD-{Guid.NewGuid():N}", "cust-enum");
        booking.LifecycleStatus = status;

        await using (var ctx = fixture.CreateContext(tenantId))
            await new BookingRepository(ctx).AddAsync(booking);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var reloaded = await new BookingRepository(ctx).GetByIdAsync(tenantId, bookingId);

            Assert.NotNull(reloaded);
            Assert.Equal(status, reloaded.LifecycleStatus);
        }
    }

    [Fact]
    public async Task VersionLookup_ChecksCurrentAndExpectedVersion()
    {
        var tenantId = $"t8-booking-version-{Guid.NewGuid():N}";
        var bookingId = "bk-version";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var booking = MakeBooking(tenantId, bookingId, "CORD-VERSION", "cust-version");
            booking.Version = 7;

            ctx.Bookings.Add(booking);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var repo = new BookingRepository(ctx);

            Assert.Equal(7, await repo.GetCurrentVersionAsync(tenantId, bookingId));
            Assert.True(await repo.HasExpectedVersionAsync(tenantId, bookingId, 7));
            Assert.False(await repo.HasExpectedVersionAsync(tenantId, bookingId, 6));
            Assert.Null(await repo.GetCurrentVersionAsync(tenantId, "missing"));
        }
    }

    [Fact]
    public async Task TryTransitionLifecycleAsync_UsesExpectedVersionAndBumpsVersion()
    {
        var tenantId = $"t8-booking-transition-{Guid.NewGuid():N}";
        var bookingId = "bk-transition";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var booking = MakeBooking(tenantId, bookingId, "CORD-TRANSITION", "cust-version");
            booking.Version = 7;

            ctx.Bookings.Add(booking);
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var repo = new BookingRepository(ctx);
            var won = await repo.TryTransitionLifecycleAsync(
                tenantId,
                bookingId,
                expectedVersion: 7,
                BookingLifecycleStatus.CancellationPending,
                DateTimeOffset.UtcNow);
            var lost = await repo.TryTransitionLifecycleAsync(
                tenantId,
                bookingId,
                expectedVersion: 7,
                BookingLifecycleStatus.Cancelled,
                DateTimeOffset.UtcNow);

            Assert.Equal(1, won);
            Assert.Equal(0, lost);
        }

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var reloaded = await ctx.Bookings.AsNoTracking()
                .SingleAsync(b => b.TenantId == tenantId && b.BookingId == bookingId);

            Assert.Equal(BookingLifecycleStatus.CancellationPending, reloaded.LifecycleStatus);
            Assert.Equal(8, reloaded.Version);
        }
    }

    [Fact]
    public async Task BookingReference_IsUniqueWithinTenant()
    {
        var tenantId = $"t8-booking-uq-{Guid.NewGuid():N}";
        var reference = "CORD-UNIQUE";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.Bookings.Add(MakeBooking(tenantId, "bk-one", reference, "cust-one"));
            ctx.Bookings.Add(MakeBooking(tenantId, "bk-two", reference, "cust-two"));

            await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        }
    }

    private static Booking MakeBooking(
        string tenantId,
        string bookingId,
        string bookingReference,
        string customerId)
    {
        var now = DateTimeOffset.UtcNow;

        return new Booking
        {
            TenantId = tenantId,
            BookingId = bookingId,
            BookingReference = bookingReference,
            ProviderReference = $"provider-{bookingId}",
            Version = 0,
            LifecycleStatus = BookingLifecycleStatus.Confirmed,
            CustomerId = customerId,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
