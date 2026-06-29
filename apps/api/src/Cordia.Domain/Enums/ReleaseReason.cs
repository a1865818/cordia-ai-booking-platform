using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Reason a capacity hold was released, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReleaseReason
{
    [PgName("expired")]
    [JsonStringEnumMemberName("expired")]
    Expired,

    [PgName("finalized")]
    [JsonStringEnumMemberName("finalized")]
    Finalized,

    [PgName("compensated")]
    [JsonStringEnumMemberName("compensated")]
    Compensated,

    [PgName("superseded")]
    [JsonStringEnumMemberName("superseded")]
    Superseded,

    [PgName("provider_released")]
    [JsonStringEnumMemberName("provider_released")]
    ProviderReleased,
}
