using Cordia.Domain.Enums;
using Cordia.Domain.Generated;

namespace Cordia.Domain.Bookings;

/// <summary>Verification-leveled booking read projection for Manage Booking reads.</summary>
public sealed record BookingProjection(
    string BookingId,
    string OperationId,
    string? BookingReference,
    string? BusinessName,
    DateOnly? BookingDate,
    string? ApproxTime,
    DateTimeOffset? StartsAt,
    int? PartySize,
    string LifecycleStatus,
    OperationStatus OperationStatus,
    OperationTypeEnum OperationType,
    long Version,
    DateTimeOffset UpdatedAt);
