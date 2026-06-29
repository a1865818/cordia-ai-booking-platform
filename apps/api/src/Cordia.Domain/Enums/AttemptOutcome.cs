using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Outcome of a workflow attempt, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AttemptOutcome
{
    [PgName("planned")]
    [JsonStringEnumMemberName("planned")]
    Planned,

    [PgName("dispatching")]
    [JsonStringEnumMemberName("dispatching")]
    Dispatching,

    [PgName("succeeded")]
    [JsonStringEnumMemberName("succeeded")]
    Succeeded,

    [PgName("rejected")]
    [JsonStringEnumMemberName("rejected")]
    Rejected,

    [PgName("transient_failure")]
    [JsonStringEnumMemberName("transient_failure")]
    TransientFailure,

    [PgName("ambiguous")]
    [JsonStringEnumMemberName("ambiguous")]
    Ambiguous,

    [PgName("reconciled")]
    [JsonStringEnumMemberName("reconciled")]
    Reconciled,

    [PgName("abandoned")]
    [JsonStringEnumMemberName("abandoned")]
    Abandoned,
}
