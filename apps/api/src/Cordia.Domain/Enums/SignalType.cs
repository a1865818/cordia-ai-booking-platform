using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Type of ingress signal used to resolve a tenant channel endpoint, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SignalType
{
    [PgName("phone_number")]
    [JsonStringEnumMemberName("phone_number")]
    PhoneNumber,

    [PgName("messaging_recipient")]
    [JsonStringEnumMemberName("messaging_recipient")]
    MessagingRecipient,

    [PgName("widget_key")]
    [JsonStringEnumMemberName("widget_key")]
    WidgetKey,

    [PgName("email_recipient")]
    [JsonStringEnumMemberName("email_recipient")]
    EmailRecipient,

    [PgName("web_origin")]
    [JsonStringEnumMemberName("web_origin")]
    WebOrigin,

    [PgName("directory_slug")]
    [JsonStringEnumMemberName("directory_slug")]
    DirectorySlug,
}
