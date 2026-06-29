using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>State machine state of a preorder saga, stored as a PostgreSQL enum.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PreorderSagaState
{
    [PgName("revalidating")]
    [JsonStringEnumMemberName("revalidating")]
    Revalidating,

    [PgName("holding_capacity")]
    [JsonStringEnumMemberName("holding_capacity")]
    HoldingCapacity,

    [PgName("reserving_items")]
    [JsonStringEnumMemberName("reserving_items")]
    ReservingItems,

    [PgName("authorizing_payment")]
    [JsonStringEnumMemberName("authorizing_payment")]
    AuthorizingPayment,

    [PgName("committing_booking")]
    [JsonStringEnumMemberName("committing_booking")]
    CommittingBooking,

    [PgName("committing_preorder")]
    [JsonStringEnumMemberName("committing_preorder")]
    CommittingPreorder,

    [PgName("compensating_items")]
    [JsonStringEnumMemberName("compensating_items")]
    CompensatingItems,

    [PgName("compensating_hold")]
    [JsonStringEnumMemberName("compensating_hold")]
    CompensatingHold,

    [PgName("compensating_booking")]
    [JsonStringEnumMemberName("compensating_booking")]
    CompensatingBooking,

    [PgName("reconciling")]
    [JsonStringEnumMemberName("reconciling")]
    Reconciling,

    [PgName("completed")]
    [JsonStringEnumMemberName("completed")]
    Completed,

    [PgName("failed_final")]
    [JsonStringEnumMemberName("failed_final")]
    FailedFinal,

    [PgName("manual_review")]
    [JsonStringEnumMemberName("manual_review")]
    ManualReview,
}
