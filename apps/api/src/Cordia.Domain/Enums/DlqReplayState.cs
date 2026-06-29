using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Replay state of a dead-letter message, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DlqReplayState
{
    [PgName("quarantined")]
    [JsonStringEnumMemberName("quarantined")]
    Quarantined,

    [PgName("replayed")]
    [JsonStringEnumMemberName("replayed")]
    Replayed,

    [PgName("discarded")]
    [JsonStringEnumMemberName("discarded")]
    Discarded,
}
