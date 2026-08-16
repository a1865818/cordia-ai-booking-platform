using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Whether the guest attended, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AttendanceStatus
{
    [PgName("unknown")]
    [JsonStringEnumMemberName("unknown")]
    Unknown,

    [PgName("attended")]
    [JsonStringEnumMemberName("attended")]
    Attended,

    [PgName("no_show")]
    [JsonStringEnumMemberName("no_show")]
    NoShow,
}
