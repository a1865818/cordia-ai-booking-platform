namespace Cordia.Domain.Waitlist;

/// <summary>
/// The constraints of a freed-up opening that waitlist matching searches against (spec 11 §5.1).
/// An eligible enrollment must target the same <see cref="LocationId"/> / <see cref="ServiceId"/>,
/// fit within <see cref="AvailableSeats"/>, and accept a <see cref="SlotStartAt"/> inside its
/// <c>[AcceptableFrom, AcceptableTo]</c> window. <see cref="SlotId"/> identifies the specific
/// opening so an enrollee who already received an offer for it (declined/expired) is not re-offered
/// the same slot on re-match. See <see cref="Interfaces.IWaitlistEnrollmentRepository.FindEligibleAsync"/>.
/// </summary>
public sealed record WaitlistMatchCriteria(
    string LocationId,
    string ServiceId,
    string SlotId,
    DateTimeOffset SlotStartAt,
    int AvailableSeats);
