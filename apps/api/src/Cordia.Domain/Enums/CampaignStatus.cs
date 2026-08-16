using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Life-cycle status of an outbound campaign, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CampaignStatus
{
    [PgName("awaiting")]
    [JsonStringEnumMemberName("awaiting")]
    Awaiting,

    [PgName("scheduled")]
    [JsonStringEnumMemberName("scheduled")]
    Scheduled,

    [PgName("sent")]
    [JsonStringEnumMemberName("sent")]
    Sent,

    [PgName("rejected")]
    [JsonStringEnumMemberName("rejected")]
    Rejected,
}
