using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Life-cycle status of a capacity hold, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HoldStatus
{
    [PgName("active")]
    [JsonStringEnumMemberName("active")]
    Active,

    [PgName("released")]
    [JsonStringEnumMemberName("released")]
    Released,

    [PgName("expired")]
    [JsonStringEnumMemberName("expired")]
    Expired,

    [PgName("finalized")]
    [JsonStringEnumMemberName("finalized")]
    Finalized,

    [PgName("reconciling")]
    [JsonStringEnumMemberName("reconciling")]
    Reconciling,
}
