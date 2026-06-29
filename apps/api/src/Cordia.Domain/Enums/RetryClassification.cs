using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Retry classification for a workflow attempt, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RetryClassification
{
    [PgName("transient")]
    [JsonStringEnumMemberName("transient")]
    Transient,

    [PgName("ambiguous")]
    [JsonStringEnumMemberName("ambiguous")]
    Ambiguous,

    [PgName("no_capacity")]
    [JsonStringEnumMemberName("no_capacity")]
    NoCapacity,

    [PgName("validation")]
    [JsonStringEnumMemberName("validation")]
    Validation,

    [PgName("authentication")]
    [JsonStringEnumMemberName("authentication")]
    Authentication,

    [PgName("authorization")]
    [JsonStringEnumMemberName("authorization")]
    Authorization,

    [PgName("payment_action")]
    [JsonStringEnumMemberName("payment_action")]
    PaymentAction,

    [PgName("policy")]
    [JsonStringEnumMemberName("policy")]
    Policy,

    [PgName("conflict")]
    [JsonStringEnumMemberName("conflict")]
    Conflict,

    [PgName("permanent_provider")]
    [JsonStringEnumMemberName("permanent_provider")]
    PermanentProvider,

    [PgName("internal_defect")]
    [JsonStringEnumMemberName("internal_defect")]
    InternalDefect,
}
