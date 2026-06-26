using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Communication channel for a tenant session, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TenantChannel
{
    [PgName("voice")]
    [JsonStringEnumMemberName("voice")]
    Voice,

    [PgName("messaging")]
    [JsonStringEnumMemberName("messaging")]
    Messaging,

    [PgName("web")]
    [JsonStringEnumMemberName("web")]
    Web,

    [PgName("email")]
    [JsonStringEnumMemberName("email")]
    Email,
}
