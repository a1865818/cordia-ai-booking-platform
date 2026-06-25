using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>Mutable booking operation row with optimistic-concurrency version token.</summary>
public class BookingOperation : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string OperationId { get; set; } = string.Empty;

    public string IntentId { get; set; } = string.Empty;

    public int IntentRevision { get; set; }

    public OperationTypeEnum OperationType { get; set; }

    public OperationStatus Status { get; set; }

    public string? SupersededOperationId { get; set; }

    public string? BookingId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Optimistic-concurrency token (§22.1); default 0.</summary>
    public long Version { get; set; }
}
