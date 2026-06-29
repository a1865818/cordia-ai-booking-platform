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

    public string CustomerId { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
