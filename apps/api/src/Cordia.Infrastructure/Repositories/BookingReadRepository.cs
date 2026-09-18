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
public sealed class BookingReadRepository(
    ApplicationDbContext db,
    ITenantDisplayNameRepository displayNames,
    IGuestContactRepository guestContacts) : IBookingReadRepository
{
    public async Task<IReadOnlyList<ManageBookingCandidate>> FindManageBookingCandidatesAsync(
        string tenantId,
        ManageBookingDiscoveryHints hints,
        int limit = 10,
        CancellationToken ct = default)
    {
        if (!HasAnyHint(hints) || limit <= 0)
            return [];

        var businessName = await BusinessNameAsync(tenantId, ct);

        // New payloads carry only an opaque `contact_ref` — the contact itself lives in
        // `guest_contacts` so it can be erased (owner retention ruling 2026-08-06). Resolve the
        // contact hints to the refs they map to, once, rather than dereferencing per candidate row.
        // An erased contact resolves to nothing, so its bookings correctly stop being findable by
        // contact: that is what erasure means, not a bug.
        //
        // BOTH channels are resolved. Under this shape the payload holds no phone AND no email, so
        // a channel with no resolver here is a channel by which the booking cannot be found at all
        // — an email-only guest would be invisible to every hint the API accepts. A caller_id hint
        // is a phone and resolves through the phone path below.
        var phoneHint = !string.IsNullOrWhiteSpace(hints.PhoneE164) ? hints.PhoneE164 : hints.CallerId;

        var contactRefs = new List<string>();
        if (!string.IsNullOrWhiteSpace(phoneHint))
            contactRefs.AddRange(await guestContacts.FindRefsByPhoneAsync(tenantId, phoneHint, ct));
        if (!string.IsNullOrWhiteSpace(hints.Email))
            contactRefs.AddRange(await guestContacts.FindRefsByEmailAsync(tenantId, hints.Email, ct));

        var rows = await BaseRows(tenantId)
            .OrderByDescending(r => r.UpdatedAt)
            .ToListAsync(ct);

        return rows
            .Select(r => ToProjection(r, businessName))
            .Where(p => p is not null && MatchesHints(p.Payload, p.Operation, hints, contactRefs))
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
        var businessName = await BusinessNameAsync(tenantId, ct);
        var rows = await BaseRows(tenantId)
            .OrderByDescending(r => r.UpdatedAt)
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            var projection = ToProjection(row, businessName);
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

        return row is null
            ? null
            : ToProjection(row, await BusinessNameAsync(tenantId, ct))?.Projection;
    }

    /// <summary>
    /// The tenant's guest-facing venue name, or null when it has not set one.
    ///
    /// Null is a real answer and is NOT backfilled from the tenant id: `business_name` is shown
    /// to a guest (spec 08 §4), and a slug would be worse than the candidate being withheld.
    /// Read once per query rather than per row.
    /// </summary>
    private Task<string?> BusinessNameAsync(string tenantId, CancellationToken ct)
        => displayNames.GetAsync(tenantId, ct);

    /// <summary>
    /// Operations joined to their confirmed revision, plus the authoritative
    /// <c>booking_reference</c> from <c>bookings</c>.
    ///
    /// The <c>bookings</c> join is a LEFT join and load-bearing: the reference is written at
    /// finalize and lives ONLY on the booking row — it is not a field of <c>BookingRequest</c>,
    /// so it can never appear in the canonical payload. Without this join
    /// <see cref="ToProjection"/> could not satisfy the reference half of a
    /// <see cref="ManageBookingCandidate"/>, and the phone lookup returned nothing for every
    /// real booking. Left, not inner, because an operation that has not reached finalize has no
    /// booking row and must still project.
    /// </summary>
    private IQueryable<BookingReadRow> BaseRows(string tenantId)
        => from op in db.BookingOperations.AsNoTracking()
           join rev in db.BookingIntentRevisions.AsNoTracking()
               on new { op.TenantId, op.IntentId, Revision = op.IntentRevision }
               equals new { rev.TenantId, rev.IntentId, rev.Revision }
           join bk in db.Bookings.AsNoTracking()
               on new { op.TenantId, BookingId = op.BookingId! }
               equals new { bk.TenantId, bk.BookingId } into bookings
           from booking in bookings.DefaultIfEmpty()
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
               BookingReference = booking != null ? booking.BookingReference : null,
               BookingLifecycle = booking != null ? booking.LifecycleStatus : null,
           };

    private static bool HasAnyHint(ManageBookingDiscoveryHints hints)
        => !string.IsNullOrWhiteSpace(hints.CallerId)
           || !string.IsNullOrWhiteSpace(hints.PhoneE164)
           || !string.IsNullOrWhiteSpace(hints.Email)
           || !string.IsNullOrWhiteSpace(hints.BookingReference);

    private static BookingProjectionParts? ToProjection(BookingReadRow row, string? businessName)
    {
        var payload = CanonicalBookingPayload.Parse(row.CanonicalPayload);
        if (payload is null)
            return null;

        var bookingId = FirstNonEmpty(payload.TargetBookingId, row.BookingId, payload.BookingId);
        if (bookingId is null)
            return null;

        // The bookings row wins: it is where finalize writes the reference. The payload spelling
        // is only a fallback for historical rows that happened to carry one.
        var bookingReference = FirstNonEmpty(row.BookingReference, payload.BookingReference);
        // The tenant's own display name — never derived from the tenant id, which would show a
        // guest a slug. A payload-carried name still wins for a historical row that has one.
        var business = FirstNonEmpty(payload.BusinessName, businessName);

        // The bookings row knows what actually happened to the booking; the operation row only
        // knows what THIS operation did. Prefer the former whenever finalize has written one.
        var lifecycleStatus = row.BookingLifecycle is { } actual
            ? ToLifecycleLabel(actual)
            : ToLifecycleStatus(row.OperationType, row.Status);
        var projection = new BookingProjection(
            bookingId,
            row.OperationId,
            bookingReference,
            business,
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
        if (bookingReference is not null
            && business is not null
            && payload.BookingDate.HasValue
            && payload.ApproxTime is not null
            && payload.PartySize.HasValue)
        {
            candidate = new ManageBookingCandidate(
                bookingId,
                bookingReference,
                business,
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
        ManageBookingDiscoveryHints hints,
        IReadOnlyCollection<string> contactRefs)
    {
        if (!string.IsNullOrWhiteSpace(hints.BookingReference)
            && MatchesAny(hints.BookingReference, row.BookingReference, payload.BookingReference, payload.BookingId, row.BookingId))
        {
            return true;
        }

        // The current shape: the payload carries an opaque ref and the phone lives in
        // `guest_contacts`. Matched by ref rather than by value, so the immutable payload never
        // has to hold the number at all.
        if (!string.IsNullOrWhiteSpace(payload.ContactRef)
            && contactRefs.Contains(payload.ContactRef, StringComparer.Ordinal))
        {
            return true;
        }

        // The legacy shape, still matched because revisions written before the retention ruling
        // are immutable and cannot be migrated. Phone hints normalise BOTH sides before comparing:
        // a booking captured as "+61 400 111 222" would otherwise be permanently unfindable by
        // "+61400111222". Formatting only — no trunk-prefix-to-country-code guessing.
        if (!string.IsNullOrWhiteSpace(hints.PhoneE164)
            && MatchesAnyPhone(hints.PhoneE164, payload.PhoneE164, payload.ContactPhone, payload.CallerId))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(hints.CallerId)
            && MatchesAnyPhone(hints.CallerId, payload.CallerId, payload.PhoneE164, payload.ContactPhone))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(hints.Email)
               && MatchesAny(hints.Email, payload.Email, payload.ContactEmail);
    }

    private static bool MatchesAnyPhone(string? hint, params string?[] values)
        => values.Any(v => PhoneMatching.Matches(hint, v));

    private static bool MatchesAny(string? hint, params string?[] values)
        => !string.IsNullOrWhiteSpace(hint)
           && values.Any(v => string.Equals(v, hint, StringComparison.OrdinalIgnoreCase));

    private static string ToLifecycleStatus(OperationTypeEnum operationType, OperationStatus status)
        => BookingLifecycleProjection.From(operationType, status);

    /// <summary>The wire label for a persisted lifecycle, using the same constants the projection emits.</summary>
    private static string ToLifecycleLabel(BookingLifecycleStatus status)
        => status switch
        {
            BookingLifecycleStatus.Cancelled => BookingLifecycleProjection.Cancelled,
            BookingLifecycleStatus.CancellationPending => BookingLifecycleProjection.CancellationPending,
            BookingLifecycleStatus.ModificationPending => BookingLifecycleProjection.ModificationPending,
            _ => BookingLifecycleProjection.Confirmed,
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

        /// <summary>
        /// From the joined <c>bookings</c> row; null until finalize writes one. This is the
        /// AUTHORITATIVE lifecycle. Deriving it from the operation row instead meant a cancelled
        /// booking still projected as confirmed: the cancel operation's payload carries only the
        /// target id, so its candidate is null and drops out, and the original create row
        /// (Create + Booked => confirmed) is the one that survives GroupBy.
        /// </summary>
        public BookingLifecycleStatus? BookingLifecycle { get; init; }

        /// <summary>From the joined <c>bookings</c> row; null until finalize writes one.</summary>
        public string? BookingReference { get; init; }
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
}
