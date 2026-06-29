using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Processing outcome for a consumer inbox message, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InboxOutcome
{
    [PgName("admitted")]
    [JsonStringEnumMemberName("admitted")]
    Admitted,

    [PgName("completed")]
    [JsonStringEnumMemberName("completed")]
    Completed,

    [PgName("rejected")]
    [JsonStringEnumMemberName("rejected")]
    Rejected,
}
