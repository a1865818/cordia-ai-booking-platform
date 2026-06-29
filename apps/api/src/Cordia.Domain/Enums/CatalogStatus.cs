using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Life-cycle status of a vendor catalog, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CatalogStatus
{
    [PgName("uploaded")]
    [JsonStringEnumMemberName("uploaded")]
    Uploaded,

    [PgName("scanning")]
    [JsonStringEnumMemberName("scanning")]
    Scanning,

    [PgName("quarantined")]
    [JsonStringEnumMemberName("quarantined")]
    Quarantined,

    [PgName("extracting")]
    [JsonStringEnumMemberName("extracting")]
    Extracting,

    [PgName("draft_ready")]
    [JsonStringEnumMemberName("draft_ready")]
    DraftReady,

    [PgName("in_review")]
    [JsonStringEnumMemberName("in_review")]
    InReview,

    [PgName("approved")]
    [JsonStringEnumMemberName("approved")]
    Approved,

    [PgName("published")]
    [JsonStringEnumMemberName("published")]
    Published,

    [PgName("superseded")]
    [JsonStringEnumMemberName("superseded")]
    Superseded,
}
