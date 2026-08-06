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

    /// <summary>
    /// Ingestion entered <c>scanning</c>/<c>extracting</c> and never left within the stall
    /// threshold. Terminal.
    ///
    /// <para>The tenth value. D4 originally froze this enum at nine and explicitly excluded
    /// <c>extract_failed</c>, which left a stalled extraction indistinguishable from a slow one
    /// forever — the §5.2 diagram had always modelled this terminal and the frozen enum could not
    /// express it. Amended by owner ruling 2026-08-06; appended, never reordered, because the
    /// pg-enum's declared order is its sort order.</para>
    /// </summary>
    [PgName("extract_failed")]
    [JsonStringEnumMemberName("extract_failed")]
    ExtractFailed,
}
