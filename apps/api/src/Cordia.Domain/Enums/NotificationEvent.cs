using System.Text.Json.Serialization;
using NpgsqlTypes;

namespace Cordia.Domain.Enums;

/// <summary>
/// Required-communication catalogue event types (spec 10 §5 / §18.4), stored as a PostgreSQL enum.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NotificationEvent
{
    [PgName("request_accepted")]
    [JsonStringEnumMemberName("request_accepted")]
    RequestAccepted,

    [PgName("still_pending")]
    [JsonStringEnumMemberName("still_pending")]
    StillPending,

    [PgName("action_required")]
    [JsonStringEnumMemberName("action_required")]
    ActionRequired,

    [PgName("booking_confirmed")]
    [JsonStringEnumMemberName("booking_confirmed")]
    BookingConfirmed,

    [PgName("no_capacity_alternatives")]
    [JsonStringEnumMemberName("no_capacity_alternatives")]
    NoCapacityAlternatives,

    [PgName("waitlist_offer")]
    [JsonStringEnumMemberName("waitlist_offer")]
    WaitlistOffer,

    [PgName("final_failure")]
    [JsonStringEnumMemberName("final_failure")]
    FinalFailure,

    [PgName("cancel_or_modify_confirmed")]
    [JsonStringEnumMemberName("cancel_or_modify_confirmed")]
    CancelOrModifyConfirmed,
}
