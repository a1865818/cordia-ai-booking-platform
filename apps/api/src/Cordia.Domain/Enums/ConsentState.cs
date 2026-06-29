using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Consent outcome stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConsentState
{
    [PgName("granted")]
    [JsonStringEnumMemberName("granted")]
    Granted,

    [PgName("declined")]
    [JsonStringEnumMemberName("declined")]
    Declined,

    [PgName("not_required")]
    [JsonStringEnumMemberName("not_required")]
    NotRequired,

    [PgName("alternative_offered")]
    [JsonStringEnumMemberName("alternative_offered")]
    AlternativeOffered,
}
