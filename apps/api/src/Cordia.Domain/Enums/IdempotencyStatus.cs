using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Processing status of an idempotency record, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IdempotencyStatus
{
    [PgName("in_progress")]
    [JsonStringEnumMemberName("in_progress")]
    InProgress,

    [PgName("completed")]
    [JsonStringEnumMemberName("completed")]
    Completed,

    [PgName("failed")]
    [JsonStringEnumMemberName("failed")]
    Failed,
}
