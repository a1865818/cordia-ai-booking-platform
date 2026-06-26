using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Status of a scheduled job, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScheduledJobStatus
{
    [PgName("pending")]
    [JsonStringEnumMemberName("pending")]
    Pending,

    [PgName("claimed")]
    [JsonStringEnumMemberName("claimed")]
    Claimed,

    [PgName("done")]
    [JsonStringEnumMemberName("done")]
    Done,

    [PgName("dead")]
    [JsonStringEnumMemberName("dead")]
    Dead,
}
