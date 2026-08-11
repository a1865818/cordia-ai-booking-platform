using System.Globalization;
using System.Text.Json;

namespace Cordia.Infrastructure.Repositories;

/// <summary>
/// Tolerant reader for the confirmed intent revision's canonical payload.
///
/// The payload is the agent's submitted <c>BookingRequest</c> stored verbatim, so field
/// names vary with the producing schema version. Every accessor accepts the snake_case and
/// camelCase spellings seen in practice and searches nested objects/arrays, returning
/// <c>null</c> rather than throwing when a field is absent — a read projection must never
/// fail a whole list because one historical row is shaped differently.
///
/// Extracted from <see cref="BookingReadRepository"/> so <see cref="ConsoleReadModel"/>
/// reuses the same parser rather than growing a second, divergent one.
/// </summary>
internal sealed record CanonicalBookingPayload(
    string? BookingId,
    string? TargetBookingId,
    string? BookingReference,
    string? BusinessName,
    DateOnly? BookingDate,
    string? ApproxTime,
    DateTimeOffset? StartsAt,
    int? PartySize,
    int? DurationMinutes,
    string? ServiceId,
    string? LocationId,
    string? PhoneE164,
    string? ContactPhone,
    string? CallerId,
    string? Email,
    string? ContactEmail,
    /// <summary>
    /// Opaque handle to contact held in <c>guest_contacts</c> (owner retention ruling
    /// 2026-08-06). New payloads carry this INSTEAD of the inline contact fields above; those
    /// stay parsed because revisions written before the ruling are immutable and still have to be
    /// searchable.
    /// </summary>
    string? ContactRef)
{
    private static readonly string[] BookingIdNames = ["booking_id", "bookingId"];
    private static readonly string[] TargetBookingIdNames = ["target_booking_id", "targetBookingId"];
    private static readonly string[] BookingReferenceNames = ["booking_reference", "bookingReference", "reference"];
    private static readonly string[] BusinessNames = ["business_name", "businessName", "business", "venue_name", "restaurant_name"];
    private static readonly string[] BookingDateNames = ["booking_date", "bookingDate", "date"];
    private static readonly string[] ApproxTimeNames = ["approx_time", "approxTime", "time", "booking_time", "bookingTime"];
    private static readonly string[] StartsAtNames = ["starts_at", "startsAt", "start_at", "startAt", "scheduled_at", "requested_at", "requestedAt"];
    private static readonly string[] PartySizeNames = ["party_size", "partySize", "guest_count", "guestCount", "guests", "people"];
    private static readonly string[] DurationNames = ["duration_minutes", "durationMinutes", "duration"];
    private static readonly string[] ServiceIdNames = ["service_id", "serviceId", "service"];
    private static readonly string[] LocationIdNames = ["location_id", "locationId", "location"];
    private static readonly string[] PhoneNames = ["phone_e164", "phoneE164", "phone"];
    private static readonly string[] ContactPhoneNames = ["contact_phone", "contactPhone"];
    private static readonly string[] CallerIdNames = ["caller_id", "callerId"];
    private static readonly string[] EmailNames = ["email"];
    private static readonly string[] ContactEmailNames = ["contact_email", "contactEmail"];
    private static readonly string[] ContactRefNames = ["contact_ref", "contactRef"];

    public static CanonicalBookingPayload? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var startsAt = ReadDateTimeOffset(root, StartsAtNames);
            DateOnly? bookingDate = ReadDate(root, BookingDateNames);
            if (bookingDate is null && startsAt.HasValue)
                bookingDate = DateOnly.FromDateTime(startsAt.Value.DateTime);

            return new CanonicalBookingPayload(
                ReadString(root, BookingIdNames),
                ReadString(root, TargetBookingIdNames),
                ReadString(root, BookingReferenceNames),
                ReadString(root, BusinessNames),
                bookingDate,
                ReadString(root, ApproxTimeNames) ?? FormatApproxTime(startsAt),
                startsAt,
                ReadInt(root, PartySizeNames),
                ReadInt(root, DurationNames),
                ReadString(root, ServiceIdNames),
                ReadString(root, LocationIdNames),
                ReadString(root, PhoneNames),
                ReadString(root, ContactPhoneNames),
                ReadString(root, CallerIdNames),
                ReadString(root, EmailNames),
                ReadString(root, ContactEmailNames),
                ReadString(root, ContactRefNames));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, IReadOnlyCollection<string> names)
    {
        if (TryFindProperty(element, names, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => EmptyToNull(value.GetString()),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };
        }

        return null;
    }

    private static int? ReadInt(JsonElement element, IReadOnlyCollection<string> names)
    {
        if (!TryFindProperty(element, names, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;

        return value.ValueKind == JsonValueKind.String
               && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }

    private static DateOnly? ReadDate(JsonElement element, IReadOnlyCollection<string> names)
    {
        var raw = ReadString(element, names);
        return DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? (DateOnly?)date
            : null;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, IReadOnlyCollection<string> names)
    {
        var raw = ReadString(element, names);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? (DateTimeOffset?)value
            : null;
    }

    private static string? FormatApproxTime(DateTimeOffset? startsAt)
        => startsAt?.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool TryFindProperty(
        JsonElement element,
        IReadOnlyCollection<string> names,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }

                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    && TryFindProperty(property.Value, names, out value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindProperty(item, names, out value))
                    return true;
            }
        }

        value = default;
        return false;
    }
}
