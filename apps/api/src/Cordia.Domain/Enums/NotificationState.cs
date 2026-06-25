using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Delivery life-cycle state of a notification row, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NotificationState
{
    [PgName("pending")]
    [JsonStringEnumMemberName("pending")]
    Pending,

    [PgName("sending")]
    [JsonStringEnumMemberName("sending")]
    Sending,

    [PgName("accepted")]
    [JsonStringEnumMemberName("accepted")]
    Accepted,

    [PgName("delivered")]
    [JsonStringEnumMemberName("delivered")]
    Delivered,

    [PgName("failed_transient")]
    [JsonStringEnumMemberName("failed_transient")]
    FailedTransient,

    [PgName("failed_permanent")]
    [JsonStringEnumMemberName("failed_permanent")]
    FailedPermanent,

    [PgName("suppressed_duplicate")]
    [JsonStringEnumMemberName("suppressed_duplicate")]
    SuppressedDuplicate,

    [PgName("invalid_recipient")]
    [JsonStringEnumMemberName("invalid_recipient")]
    InvalidRecipient,
}
