using Cordia.Application.Handlers.ManageBooking.FindManageableBookings;
using Cordia.Domain.Bookings;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;

namespace Cordia.Tests.UnitTests.ManageBooking;

/// <summary>
/// A booking the guest has already cancelled is not a booking they can manage. The read
/// repository returns every matching row regardless of lifecycle, and the candidate contract
/// carries no status, so the filter has to happen here or the caller cannot tell them apart.
/// </summary>
[Trait("Category", "Unit")]
public class FindManageableBookingsFilterTests
{
    private sealed class FakeRepository(params ManageBookingCandidate[] rows) : IBookingReadRepository
    {
        public Task<IReadOnlyList<ManageBookingCandidate>> FindManageBookingCandidatesAsync(
            string tenantId, ManageBookingDiscoveryHints hints, int limit = 10, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ManageBookingCandidate>>(rows);

        public Task<BookingProjection?> GetBookingProjectionByBookingIdAsync(
            string tenantId, string bookingId, CancellationToken ct = default)
            => Task.FromResult<BookingProjection?>(null);

        public Task<BookingProjection?> GetBookingProjectionByOperationIdAsync(
            string tenantId, string operationId, CancellationToken ct = default)
            => Task.FromResult<BookingProjection?>(null);
    }

    private static ManageBookingCandidate Row(string id, string lifecycle)
        => new(id, $"REF-{id}", "Marlowe & Fig", new DateOnly(2026, 9, 16), "19:00", 2, lifecycle);

    private static Task<FindManageableBookingsResult> Run(params ManageBookingCandidate[] rows)
        => new FindManageableBookingsQueryHandler(new FakeRepository(rows))
            .Handle(new FindManageableBookingsQuery("t1", "+61400000000", null, null), TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_cancelled_booking_is_not_a_candidate()
    {
        var result = await Run(Row("bk_dead", "Cancelled"));

        Assert.Equal(FindStatus.None, result.Status);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task One_live_and_one_cancelled_is_exactly_one_candidate_not_a_question()
    {
        // The live symptom: two candidates made the agent ask for a reference for no reason.
        var result = await Run(Row("bk_dead", "cancelled"), Row("bk_live", "Confirmed"));

        Assert.Equal(FindStatus.Found, result.Status);
        Assert.Equal("bk_live", Assert.Single(result.Candidates).BookingId);
    }

    [Fact]
    public async Task A_pending_cancellation_is_not_offered_again()
    {
        var result = await Run(Row("bk_pending", "cancellation_pending"));

        Assert.Equal(FindStatus.None, result.Status);
    }

    [Theory]
    [InlineData("Confirmed")]
    [InlineData("ModificationPending")]
    [InlineData("modification_pending")]
    public async Task Live_states_stay_manageable(string lifecycle)
    {
        var result = await Run(Row("bk_1", lifecycle));

        Assert.Equal(FindStatus.Found, result.Status);
    }

    [Fact]
    public async Task A_blank_status_does_not_hide_a_booking()
    {
        // Unknown is not evidence of cancellation.
        var result = await Run(Row("bk_1", ""));

        Assert.Equal(FindStatus.Found, result.Status);
    }
}
