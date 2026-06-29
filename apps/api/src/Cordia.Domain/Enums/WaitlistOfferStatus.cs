using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Life-cycle status of a waitlist offer, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WaitlistOfferStatus
{
    [PgName("pending")]
    [JsonStringEnumMemberName("pending")]
    Pending,

    [PgName("matched")]
    [JsonStringEnumMemberName("matched")]
    Matched,

    [PgName("offered")]
    [JsonStringEnumMemberName("offered")]
    Offered,

    [PgName("accepted")]
    [JsonStringEnumMemberName("accepted")]
    Accepted,

    [PgName("declined")]
    [JsonStringEnumMemberName("declined")]
    Declined,

    [PgName("expired")]
    [JsonStringEnumMemberName("expired")]
    Expired,

    [PgName("released")]
    [JsonStringEnumMemberName("released")]
    Released,
}
