using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Operational status of a tenant channel endpoint, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EndpointStatus
{
    [PgName("active")]
    [JsonStringEnumMemberName("active")]
    Active,

    [PgName("disabled")]
    [JsonStringEnumMemberName("disabled")]
    Disabled,
}
