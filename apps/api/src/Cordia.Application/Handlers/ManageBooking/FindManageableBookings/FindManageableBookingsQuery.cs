using System.Globalization;
using Cordia.Domain.Bookings;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.ManageBooking.FindManageableBookings;

/// <summary>Query for minimized Manage Booking discovery candidates.</summary>
public sealed record FindManageableBookingsQuery(
    string TenantId,
    string? PhoneE164,
    string? BookingReference,
    string? Email) : IRequest<FindManageableBookingsResult>;

public sealed record FindManageableBookingsResult(
    FindStatus Status,
    IReadOnlyList<ManageableBooking> Candidates);

public sealed class FindManageableBookingsValidator : AbstractValidator<FindManageableBookingsQuery>
{
    public FindManageableBookingsValidator()
    {
        RuleFor(x => x)
            .Must(HasAnyHint)
            .WithMessage("At least one of phone_e164, booking_reference, or email is required.");
    }

    private static bool HasAnyHint(FindManageableBookingsQuery query)
        => !string.IsNullOrWhiteSpace(query.PhoneE164)
           || !string.IsNullOrWhiteSpace(query.BookingReference)
           || !string.IsNullOrWhiteSpace(query.Email);
}

public sealed class FindManageableBookingsQueryHandler(IBookingReadRepository bookings)
    : IRequestHandler<FindManageableBookingsQuery, FindManageableBookingsResult>
{
    public async Task<FindManageableBookingsResult> Handle(
        FindManageableBookingsQuery request,
        CancellationToken ct)
    {
        var hints = new ManageBookingDiscoveryHints(
            PhoneE164: request.PhoneE164,
            Email: request.Email,
            BookingReference: request.BookingReference);

        var candidates = await bookings.FindManageBookingCandidatesAsync(request.TenantId, hints, ct: ct);

        // A booking the guest has already cancelled is not one they can manage, and it was
        // being handed back as a candidate with no status field to tell it apart. Live effect:
        // the agent's caller-ID discovery auto-targeted the dead booking on the next "cancel my
        // booking" (harmless, the API answered already_cancelled), and a guest with one
        // cancelled and one live booking saw two candidates and was asked for a reference for
        // no reason. CancellationPending is excluded too: that guest has already asked, and
        // offering the same booking again invites a second, conflicting cancellation.
        var live = candidates.Where(c => ManageBookingMapper.IsManageable(c.LifecycleStatus)).ToList();
        var mapped = live.Select(ManageBookingMapper.ToContract).ToList();

        var status = mapped.Count switch
        {
            0 => FindStatus.None,
            1 => FindStatus.Found,
            _ => FindStatus.Multiple,
        };

        return new FindManageableBookingsResult(status, mapped);
    }
}

internal static class ManageBookingMapper
{
    /// <summary>
    /// Lifecycle states in which a guest can still do something to the booking. The candidate
    /// carries the status as the same wire label <see cref="BookingLifecycleProjection"/> emits.
    /// </summary>
    public static bool IsManageable(string? lifecycleStatus)
    {
        if (string.IsNullOrWhiteSpace(lifecycleStatus))
            return true; // unknown is not evidence of cancellation; do not hide a booking on a blank

        return !lifecycleStatus.Equals(BookingLifecycleProjection.Cancelled, StringComparison.OrdinalIgnoreCase)
            && !lifecycleStatus.Equals(BookingLifecycleProjection.CancellationPending, StringComparison.OrdinalIgnoreCase);
    }

    public static ManageableBooking ToContract(ManageBookingCandidate candidate)
        => new()
        {
            BookingId = candidate.BookingId,
            BookingReference = candidate.BookingReference,
            Business = candidate.BusinessName,
            Date = candidate.BookingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ApproxTime = candidate.ApproxTime,
            PartySize = candidate.PartySize,
        };

    /// <summary>
    /// Maps the read projection to the wire contract.
    ///
    /// <paramref name="bookingVersion"/> is the <c>bookings</c> row's optimistic-concurrency
    /// token and MUST come from <see cref="Cordia.Domain.Interfaces.IBookingRepository"/> —
    /// deliberately NOT <see cref="BookingProjection.Version"/>, which is the *operation*
    /// version (<c>booking_operations.version</c>). Modify and cancel compare
    /// <c>expected_booking_version</c> against the booking row, so returning the operation
    /// version here would hand callers a value guaranteed to fail with 409 stale_version.
    /// </summary>
    public static BookingProjectionRef ToContract(BookingProjection projection, long? bookingVersion = null)
        => new()
        {
            BookingId = projection.BookingId,
            BookingReference = projection.BookingReference!,
            Status = projection.LifecycleStatus,
            StartsAt = projection.StartsAt,
            PartySize = projection.PartySize,
            Version = bookingVersion,
        };
}
