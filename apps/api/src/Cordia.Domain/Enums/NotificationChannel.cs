using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Delivery channel for a notification, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NotificationChannel
{
    [PgName("sms")]
    [JsonStringEnumMemberName("sms")]
    Sms,

    [PgName("email")]
    [JsonStringEnumMemberName("email")]
    Email,

    [PgName("channel")]
    [JsonStringEnumMemberName("channel")]
    Channel,
}
