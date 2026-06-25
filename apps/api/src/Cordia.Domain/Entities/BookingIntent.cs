using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>Represents the stable identity anchor for a booking intent across revisions.</summary>
public class BookingIntent : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string IntentId { get; set; } = string.Empty;

    public string? SessionId { get; set; }

    public OperationTypeEnum OperationType { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string? SupersededByIntentId { get; set; }
}
