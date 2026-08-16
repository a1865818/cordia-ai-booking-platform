using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>Authoritative confirmed/manageable booking aggregate for spec 08.</summary>
public class Booking : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string BookingId { get; set; } = string.Empty;

    public string BookingReference { get; set; } = string.Empty;

    public string? ProviderReference { get; set; }

    /// <summary>Optimistic-concurrency version token.</summary>
    public long Version { get; set; }

    public BookingLifecycleStatus LifecycleStatus { get; set; }

    /// <summary>
    /// The operation that parked <see cref="BookingLifecycleStatus.ModificationPending"/> or
    /// <see cref="BookingLifecycleStatus.CancellationPending"/> here, and therefore the only one
    /// entitled to release it. <c>null</c> whenever nothing is parked.
    ///
    /// <para>The lifecycle status alone cannot say WHO is in flight, and two operations of the same
    /// type can be. <c>POST /api/Bookings/{id}/modifications</c> parks under a version-only guard
    /// with no "must be Confirmed" precondition, so a second modify presenting the already-pending
    /// version parks the booking again — after which the first one failing found its own type's
    /// pending state, matched, and cleared the SECOND one's parking. This column is the identity
    /// that check was missing.</para>
    ///
    /// <para>Written only by the two front doors that park (via
    /// <c>IBookingRepository.TryParkPendingAsync</c>) and cleared by every lifecycle transition out
    /// of a pending state.</para>
    /// </summary>
    public string? PendingOperationId { get; set; }

    public string CustomerId { get; set; } = string.Empty;

    /// <summary>Who created the booking. Null on historical rows means unknown.</summary>
    public AttributedSource? AttributedSource { get; set; }

    /// <summary>Guest attendance. Defaults to <see cref="AttendanceStatus.Unknown"/>.</summary>
    public AttendanceStatus Attendance { get; set; } = AttendanceStatus.Unknown;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
