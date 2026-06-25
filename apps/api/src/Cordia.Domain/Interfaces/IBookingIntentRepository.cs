using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for the booking intent / revision / confirmation chain (spec 04).</summary>
public interface IBookingIntentRepository
{
    Task AddIntentAsync(BookingIntent intent, CancellationToken ct = default);

    Task AddRevisionAsync(BookingIntentRevision revision, CancellationToken ct = default);

    Task AddConfirmationAsync(IntentConfirmation confirmation, CancellationToken ct = default);
}
