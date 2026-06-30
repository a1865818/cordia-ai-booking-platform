using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Lifecycle status of an authoritative booking, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BookingLifecycleStatus
{
    [PgName("confirmed")]
    [JsonStringEnumMemberName("confirmed")]
    Confirmed,

    [PgName("modification_pending")]
    [JsonStringEnumMemberName("modification_pending")]
    ModificationPending,

    [PgName("cancellation_pending")]
    [JsonStringEnumMemberName("cancellation_pending")]
    CancellationPending,

    [PgName("cancelled")]
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,
}
