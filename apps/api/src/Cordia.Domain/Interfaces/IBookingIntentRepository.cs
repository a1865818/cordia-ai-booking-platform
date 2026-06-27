using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for the booking intent / revision / confirmation chain (spec 04).</summary>
public interface IBookingIntentRepository
{
    Task AddIntentAsync(BookingIntent intent, CancellationToken ct = default);

    Task AddRevisionAsync(BookingIntentRevision revision, CancellationToken ct = default);

    Task AddConfirmationAsync(IntentConfirmation confirmation, CancellationToken ct = default);

    /// <summary>
    /// Returns <c>true</c> when a <see cref="BookingIntent"/> row already exists for
    /// <paramref name="intentId"/> under <paramref name="tenantId"/>. Explicitly scoped
    /// in addition to the ambient global query filter as belt-and-suspenders tenant
    /// isolation (spec 04 §10.3).
    /// </summary>
    Task<bool> IntentExistsAsync(string tenantId, string intentId, CancellationToken ct = default);
}
