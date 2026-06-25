using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Life-cycle status of a deposit session, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DepositStatus
{
    [PgName("created")]
    [JsonStringEnumMemberName("created")]
    Created,

    [PgName("link_sent")]
    [JsonStringEnumMemberName("link_sent")]
    LinkSent,

    [PgName("paid")]
    [JsonStringEnumMemberName("paid")]
    Paid,

    [PgName("expired")]
    [JsonStringEnumMemberName("expired")]
    Expired,
}
