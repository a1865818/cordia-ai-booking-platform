using System.Globalization;
using System.Text.Json;
using Cordia.Domain.Bookings;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IBookingReadRepository"/>.</summary>
public sealed class BookingReadRepository(ApplicationDbContext db) : IBookingReadRepository
{
    public async Task<IReadOnlyList<ManageBookingCandidate>> FindManageBookingCandidatesAsync(
        string tenantId,
        ManageBookingDiscoveryHints hints,
        int limit = 10,
        CancellationToken ct = default)
    {
        if (!HasAnyHint(hints) || limit <= 0)
            return [];

        var rows = await BaseRows(tenantId)
            .OrderByDescending(r => r.UpdatedAt)
            .ToListAsync(ct);

        return rows
            .Select(r => ToProjection(r))
            .Where(p => p is not null && MatchesHints(p.Payload, p.Operation, hints))
            .Select(p => p!.Candidate)
            .Where(c => c is not null)
            .Select(c => c!)
            .GroupBy(c => c.BookingId)
            .Select(g => g.First())
            .Take(limit)
            .ToList();
    }

    public async Task<BookingProjection?> GetBookingProjectionByBookingIdAsync(
        string tenantId,
        string bookingId,
        CancellationToken ct = default)
    {
        var rows = await BaseRows(tenantId)
            .OrderByDescending(r => r.UpdatedAt)
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            var projection = ToProjection(row);
            if (projection?.Projection is not null && projection.MatchesBookingId(bookingId))
                return projection.Projection;
        }

        return null;
    }

    public async Task<BookingProjection?> GetBookingProjectionByOperationIdAsync(
        string tenantId,
        string operationId,
        CancellationToken ct = default)
    {
        var row = await BaseRows(tenantId)
            .FirstOrDefaultAsync(r => r.OperationId == operationId, ct);

        return row is null ? null : ToProjection(row)?.Projection;
    }

    private IQueryable<BookingReadRow> BaseRows(string tenantId)
        => from op in db.BookingOperations.AsNoTracking()
           join rev in db.BookingIntentRevisions.AsNoTracking()
               on new { op.TenantId, op.IntentId, Revision = op.IntentRevision }
               equals new { rev.TenantId, rev.IntentId, rev.Revision }
           where op.TenantId == tenantId
           select new BookingReadRow
           {
               OperationId = op.OperationId,
               BookingId = op.BookingId,
               OperationType = op.OperationType,
               Status = op.Status,
               Version = op.Version,
               UpdatedAt = op.UpdatedAt,
               CanonicalPayload = rev.CanonicalPayload,
           };

    private static bool HasAnyHint(ManageBookingDiscoveryHints hints)
        => !string.IsNullOrWhiteSpace(hints.CallerId)
           || !string.IsNullOrWhiteSpace(hints.PhoneE164)
           || !string.IsNullOrWhiteSpace(hints.Email)
           || !string.IsNullOrWhiteSpace(hints.BookingReference);

    private static BookingProjectionParts? ToProjection(BookingReadRow row)
    {
        var payload = CanonicalBookingPayload.Parse(row.CanonicalPayload);
        if (payload is null)
            return null;

        var bookingId = FirstNonEmpty(payload.TargetBookingId, row.BookingId, payload.BookingId);
        if (bookingId is null)
            return null;

        var lifecycleStatus = ToLifecycleStatus(row.OperationType, row.Status);
        var projection = new BookingProjection(
            bookingId,
            row.OperationId,
            payload.BookingReference,
            payload.BusinessName,
            payload.BookingDate,
            payload.ApproxTime,
            payload.StartsAt,
            payload.PartySize,
            lifecycleStatus,
            row.Status,
            row.OperationType,
            row.Version,
            row.UpdatedAt);

        ManageBookingCandidate? candidate = null;
        if (payload.BookingReference is not null
            && payload.BusinessName is not null
            && payload.BookingDate.HasValue
            && payload.ApproxTime is not null
            && payload.PartySize.HasValue)
        {
            candidate = new ManageBookingCandidate(
                bookingId,
                payload.BookingReference,
                payload.BusinessName,
                payload.BookingDate.Value,
                payload.ApproxTime,
                payload.PartySize.Value,
                lifecycleStatus);
        }

        return new BookingProjectionParts(row, payload, projection, candidate);
    }

    private static bool MatchesHints(
        CanonicalBookingPayload payload,
        BookingReadRow row,
        ManageBookingDiscoveryHints hints)
    {
        if (!string.IsNullOrWhiteSpace(hints.BookingReference)
            && MatchesAny(hints.BookingReference, payload.BookingReference, payload.BookingId, row.BookingId))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(hints.PhoneE164)
            && MatchesAny(hints.PhoneE164, payload.PhoneE164, payload.ContactPhone, payload.CallerId))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(hints.CallerId)
            && MatchesAny(hints.CallerId, payload.CallerId, payload.PhoneE164, payload.ContactPhone))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(hints.Email)
               && MatchesAny(hints.Email, payload.Email, payload.ContactEmail);
    }

    private static bool MatchesAny(string? hint, params string?[] values)
        => !string.IsNullOrWhiteSpace(hint)
           && values.Any(v => string.Equals(v, hint, StringComparison.OrdinalIgnoreCase));

    private static string ToLifecycleStatus(OperationTypeEnum operationType, OperationStatus status)
        => status switch
        {
            OperationStatus.Booked when operationType is OperationTypeEnum.Modify or OperationTypeEnum.Reschedule
                => "modification_pending",
            OperationStatus.Booked => "confirmed",
            OperationStatus.Rejected or OperationStatus.Expired or OperationStatus.FailedFinal => "cancelled",
            _ when operationType is OperationTypeEnum.Cancel => "cancellation_pending",
            _ when operationType is OperationTypeEnum.Modify or OperationTypeEnum.Reschedule => "modification_pending",
            _ => "confirmed",
        };

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private sealed class BookingReadRow
    {
        public required string OperationId { get; init; }

        public string? BookingId { get; init; }

        public OperationTypeEnum OperationType { get; init; }

        public OperationStatus Status { get; init; }

        public long Version { get; init; }

        public DateTimeOffset UpdatedAt { get; init; }

        public required string CanonicalPayload { get; init; }
    }

    private sealed record BookingProjectionParts(
        BookingReadRow Operation,
        CanonicalBookingPayload Payload,
        BookingProjection Projection,
        ManageBookingCandidate? Candidate)
    {
        public bool MatchesBookingId(string bookingId)
            => MatchesAny(bookingId, Projection.BookingId, Operation.BookingId, Payload.BookingId, Payload.TargetBookingId);
    }

    private sealed record CanonicalBookingPayload(
        string? BookingId,
        string? TargetBookingId,
        string? BookingReference,
        string? BusinessName,
        DateOnly? BookingDate,
        string? ApproxTime,
        DateTimeOffset? StartsAt,
        int? PartySize,
        string? PhoneE164,
        string? ContactPhone,
        string? CallerId,
        string? Email,
        string? ContactEmail)
    {
        private static readonly string[] BookingIdNames = ["booking_id", "bookingId"];
        private static readonly string[] TargetBookingIdNames = ["target_booking_id", "targetBookingId"];
        private static readonly string[] BookingReferenceNames = ["booking_reference", "bookingReference", "reference"];
        private static readonly string[] BusinessNames = ["business_name", "businessName", "business", "venue_name", "restaurant_name"];
        private static readonly string[] BookingDateNames = ["booking_date", "bookingDate", "date"];
        private static readonly string[] ApproxTimeNames = ["approx_time", "approxTime", "time", "booking_time", "bookingTime"];
        private static readonly string[] StartsAtNames = ["starts_at", "startsAt", "start_at", "startAt", "scheduled_at"];
        private static readonly string[] PartySizeNames = ["party_size", "partySize", "guest_count", "guestCount", "guests", "people"];
        private static readonly string[] PhoneNames = ["phone_e164", "phoneE164", "phone"];
        private static readonly string[] ContactPhoneNames = ["contact_phone", "contactPhone"];
        private static readonly string[] CallerIdNames = ["caller_id", "callerId"];
        private static readonly string[] EmailNames = ["email"];
        private static readonly string[] ContactEmailNames = ["contact_email", "contactEmail"];

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
                    ReadString(root, PhoneNames),
                    ReadString(root, ContactPhoneNames),
                    ReadString(root, CallerIdNames),
                    ReadString(root, EmailNames),
                    ReadString(root, ContactEmailNames));
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
}
