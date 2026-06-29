using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>Life-cycle status of a booking operation, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OperationStatus
{
    [PgName("accepted")]
    [JsonStringEnumMemberName("accepted")]
    Accepted,

    [PgName("validating")]
    [JsonStringEnumMemberName("validating")]
    Validating,

    [PgName("awaiting_user_action")]
    [JsonStringEnumMemberName("awaiting_user_action")]
    AwaitingUserAction,

    [PgName("reserving_capacity")]
    [JsonStringEnumMemberName("reserving_capacity")]
    ReservingCapacity,

    [PgName("held")]
    [JsonStringEnumMemberName("held")]
    Held,

    [PgName("awaiting_payment")]
    [JsonStringEnumMemberName("awaiting_payment")]
    AwaitingPayment,

    [PgName("finalizing")]
    [JsonStringEnumMemberName("finalizing")]
    Finalizing,

    [PgName("booked")]
    [JsonStringEnumMemberName("booked")]
    Booked,

    [PgName("alternatives_required")]
    [JsonStringEnumMemberName("alternatives_required")]
    AlternativesRequired,

    [PgName("reconciling")]
    [JsonStringEnumMemberName("reconciling")]
    Reconciling,

    [PgName("compensation_required")]
    [JsonStringEnumMemberName("compensation_required")]
    CompensationRequired,

    [PgName("manual_review")]
    [JsonStringEnumMemberName("manual_review")]
    ManualReview,

    [PgName("rejected")]
    [JsonStringEnumMemberName("rejected")]
    Rejected,

    [PgName("expired")]
    [JsonStringEnumMemberName("expired")]
    Expired,

    [PgName("waitlisted")]
    [JsonStringEnumMemberName("waitlisted")]
    Waitlisted,

    [PgName("failed_final")]
    [JsonStringEnumMemberName("failed_final")]
    FailedFinal,
}
