using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Delivery priority for a notification, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessagePriority
{
    [PgName("critical")]
    [JsonStringEnumMemberName("critical")]
    Critical,

    [PgName("high")]
    [JsonStringEnumMemberName("high")]
    High,

    [PgName("normal")]
    [JsonStringEnumMemberName("normal")]
    Normal,
}
