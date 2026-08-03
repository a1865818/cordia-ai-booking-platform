using Cordia.Domain.Console;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IConsoleReadModel"/> — read-only projections for
/// the operations console.
///
/// Every query is <c>AsNoTracking</c>, filtered on <c>tenant_id</c> (Inv-13) and bounded by
/// an explicit limit. No query writes, and none introduces a table or column: the schedule,
/// customer and overview views are derived from <c>booking_operations</c> joined to the
/// confirmed <c>booking_intent_revisions</c>, read through the shared
/// <see cref="CanonicalBookingPayload"/> parser.
///
/// A booking's start time lives only inside the revision's canonical-payload JSON column
/// (there is no queryable <c>starts_at</c> on any table), so the schedule and customer
/// projections cannot push their predicate into SQL. They therefore **page** through the
/// tenant's operations and filter in memory, stopping once enough matches are found.
///
/// Truncation is explicit, never silent: if <see cref="MaxScan"/> rows are examined without
/// exhausting the tenant, the result is flagged <c>Truncated</c> so the caller can say the
/// list is partial rather than presenting it as complete.
/// </summary>
public sealed class ConsoleReadModel(ApplicationDbContext db) : IConsoleReadModel
{
    /// <summary>Rows fetched per page while scanning for payload matches.</summary>
    private const int PageSize = 500;

    /// <summary>
    /// Hard ceiling on rows examined for one request. Reaching it flags the result
    /// truncated rather than quietly returning a short list.
    /// </summary>
    private const int MaxScan = 20_000;

    public async Task<ScheduleResult> ListScheduleAsync(
        string tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken ct = default)
    {
        if (limit <= 0) return new ScheduleResult(Array.Empty<ScheduledBooking>(), false);

        var matches = new List<ParsedRow>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        // The newest row for a booking carries its current state, but not necessarily its
        // slot: a cancellation's canonical payload has no `starts_at` at all. Tracked
        // separately so a cancel row cannot consume the booking's place in the grid and make
        // the booking vanish while it is still live.
        var latestState = new Dictionary<string, ParsedRow>(StringComparer.Ordinal);
        var hitLimit = false;

        // Rows arrive newest-activity-first, so the first row seen for a booking is its
        // latest state; later rows for the same booking are stale for state purposes.
        var truncated = await ScanAsync(
            tenantId,
            parsed =>
            {
                if (!latestState.ContainsKey(parsed.Key))
                    latestState[parsed.Key] = parsed;

                var startsAt = parsed.Payload.StartsAt;
                if (!startsAt.HasValue || startsAt.Value < from || startsAt.Value >= to) return false;

                if (!placed.Add(parsed.Key)) return false;

                matches.Add(parsed);
                if (matches.Count < limit) return false;

                // Stopping because the caller's page is full is still a partial answer: rows
                // beyond it were never examined. Reporting `truncated: false` here would
                // present an incomplete week as a complete one, and because the scan runs
                // newest-activity-first the omitted bookings are arbitrary — holes appear in
                // the middle of the grid while later days render fully.
                hitLimit = true;
                return true;
            },
            ct);

        var resolveCustomer = await BuildCustomerResolverAsync(tenantId, matches, ct);

        return new ScheduleResult(
            matches
                .Select(row => ToScheduledBooking(
                    row,
                    resolveCustomer(row),
                    latestState.TryGetValue(row.Key, out var latest) ? latest : row))
                .OrderBy(b => b.StartsAt)
                .ToList(),
            truncated || hitLimit);
    }

    /// <summary>
    /// Pages through the tenant's operations newest-activity-first, handing each parsed row
    /// to <paramref name="accept"/>. Stops when <paramref name="accept"/> returns true
    /// (enough results), when the tenant is exhausted, or at <see cref="MaxScan"/> — the
    /// last of which is reported as truncation.
    /// </summary>
    private async Task<bool> ScanAsync(
        string tenantId,
        Func<ParsedRow, bool> accept,
        CancellationToken ct)
    {
        var scanned = 0;

        while (scanned < MaxScan)
        {
            var page = await PayloadRows(tenantId).Skip(scanned).Take(PageSize).ToListAsync(ct);
            if (page.Count == 0) return false; // tenant exhausted — the result is complete

            scanned += page.Count;

            foreach (var parsed in Parse(page))
            {
                if (accept(parsed)) return false; // satisfied the caller's limit
            }

            if (page.Count < PageSize) return false; // last page — exhausted
        }

        return true; // hit the ceiling with rows still unexamined
    }

    public async Task<IReadOnlyList<OperationSummary>> ListOperationsAsync(
        string tenantId,
        IReadOnlyCollection<OperationStatus> statuses,
        int limit,
        CancellationToken ct = default)
    {
        if (limit <= 0) return [];

        var query = db.BookingOperations
            .AsNoTracking()
            .Where(o => o.TenantId == tenantId);

        if (statuses.Count > 0)
        {
            var wanted = statuses.ToArray();
            query = query.Where(o => wanted.Contains(o.Status));
        }

        return await query
            .OrderByDescending(o => o.UpdatedAt)
            .Take(limit)
            .Select(o => new OperationSummary(
                o.OperationId,
                o.BookingId,
                o.IntentId,
                o.IntentRevision,
                o.OperationType,
                o.Status,
                o.StatusReason,
                o.IsPreorder,
                o.CorrelationId,
                o.CreatedAt,
                o.UpdatedAt))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ConversationSessionSummary>> ListSessionsAsync(
        string tenantId,
        int limit,
        CancellationToken ct = default)
    {
        if (limit <= 0) return [];

        return await db.ConversationSessions
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .OrderByDescending(s => s.StartedAt)
            .Take(limit)
            .Select(s => new ConversationSessionSummary(
                s.SessionId,
                s.Channel,
                s.CustomerRef,
                s.ConsentState,
                s.RecordingAllowed,
                s.StartedAt,
                s.EndedAt))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WaitlistEnrollmentSummary>> ListWaitlistEnrollmentsAsync(
        string tenantId,
        bool activeOnly,
        int limit,
        CancellationToken ct = default)
    {
        if (limit <= 0) return [];

        var query = db.WaitlistEnrollments
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId);

        if (activeOnly)
            query = query.Where(e => e.CancelledAt == null);

        var enrollments = await query
            .OrderByDescending(e => e.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

        if (enrollments.Count == 0) return [];

        var ids = enrollments.Select(e => e.EnrollmentId).ToArray();

        // Latest offer per enrollment, resolved in one round trip.
        var offers = await db.WaitlistOffers
            .AsNoTracking()
            .Where(o => o.TenantId == tenantId && ids.Contains(o.EnrollmentId))
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(ct);

        var latestByEnrollment = offers
            .GroupBy(o => o.EnrollmentId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        return enrollments
            .Select(e =>
            {
                latestByEnrollment.TryGetValue(e.EnrollmentId, out var offer);
                return new WaitlistEnrollmentSummary(
                    e.EnrollmentId,
                    e.CustomerRef,
                    e.ServiceId,
                    e.LocationId,
                    e.PartySize,
                    e.AcceptableFrom,
                    e.AcceptableTo,
                    // AutoBook is stored as a serialized grant; its presence is the
                    // authorization (spec 11 §20 — silent auto-book is prohibited by default).
                    !string.IsNullOrWhiteSpace(e.AutoBook),
                    e.CancelledAt.HasValue,
                    e.CreatedAt,
                    offer?.OfferId,
                    offer?.Status,
                    offer?.OfferExpiresAt);
            })
            .ToList();
    }

    public async Task<CustomerListResult> ListCustomersAsync(
        string tenantId,
        string? query,
        int limit,
        CancellationToken ct = default)
    {
        if (limit <= 0) return new CustomerListResult(Array.Empty<CustomerSummary>(), false);

        // Every row must be examined: a customer's bookings are scattered through the
        // history, so stopping early would undercount them and — worse for a *search* —
        // report "no such customer" for one that exists further back.
        var all = new List<ParsedRow>();
        var truncated = await ScanAsync(tenantId, parsed => { all.Add(parsed); return false; }, ct);

        var resolveCustomer = await BuildCustomerResolverAsync(tenantId, all, ct);

        var withCustomer = all
            .Select(p => (Parsed: p, CustomerRef: resolveCustomer(p)))
            .Where(x => !string.IsNullOrWhiteSpace(x.CustomerRef))
            .ToList();

        var filtered = string.IsNullOrWhiteSpace(query)
            ? withCustomer
            : withCustomer
                .Where(x => x.CustomerRef!.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        return new CustomerListResult(
            filtered
                .GroupBy(x => x.CustomerRef!, StringComparer.Ordinal)
                .Select(g =>
                {
                    // Rows arrive newest-first, so the first entry is the most recent activity.
                    var newest = g.First().Parsed;
                    return new CustomerSummary(
                        g.Key,
                        g.Select(x => x.Parsed.Key).Distinct(StringComparer.Ordinal).Count(),
                        g.Min(x => x.Parsed.Row.CreatedAt),
                        g.Max(x => x.Parsed.Row.UpdatedAt),
                        newest.Payload.ServiceId,
                        newest.BookingId);
                })
                .OrderByDescending(c => c.LastSeenAt)
                .Take(limit)
                .ToList(),
            truncated);
    }

    public async Task<BookingOverview> GetOverviewAsync(
        string tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        // Per-day counts are aggregated in SQL, so they are exact regardless of volume —
        // an analytics chart that silently undercounts is worse than no chart.
        var dayCounts = await db.BookingOperations
            .AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.CreatedAt >= from && o.CreatedAt < to)
            .GroupBy(o => o.CreatedAt.Date)
            .Select(g => new
            {
                Date = g.Key,
                Accepted = g.Count(),
                Booked = g.Count(o => o.Status == OperationStatus.Booked),
            })
            .ToListAsync(ct);

        var perDay = dayCounts
            .Select(d => new OverviewDay(DateOnly.FromDateTime(d.Date), d.Accepted, d.Booked))
            .OrderBy(d => d.Date)
            .ToList();

        // The channel and service breakdowns need the revision payload, which is not
        // queryable, so they are bounded — ordered so the truncation is deterministic
        // (newest first) rather than whatever the planner happened to return.
        var operations = await db.BookingOperations
            .AsNoTracking()
            .Where(o => o.TenantId == tenantId && o.CreatedAt >= from && o.CreatedAt < to)
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => new
            {
                o.OperationId,
                o.IntentId,
                o.IntentRevision,
                o.Status,
                o.CreatedAt,
            })
            .Take(MaxScan)
            .ToListAsync(ct);

        var breakdownTruncated = operations.Count >= MaxScan;

        // Channel comes from the session that produced the intent; an operation whose
        // revision has no source session simply does not contribute to the breakdown.
        var intentIds = operations.Select(o => o.IntentId).Distinct(StringComparer.Ordinal).ToArray();

        var revisions = intentIds.Length == 0
            ? []
            : await db.BookingIntentRevisions
                .AsNoTracking()
                .Where(r => r.TenantId == tenantId && intentIds.Contains(r.IntentId))
                .Select(r => new { r.IntentId, r.Revision, r.SourceSessionId, r.CanonicalPayload })
                .ToListAsync(ct);

        var revisionByKey = revisions
            .GroupBy(r => $"{r.IntentId}#{r.Revision}", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var sessionIds = revisions
            .Select(r => r.SourceSessionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var channelBySession = sessionIds.Length == 0
            ? new Dictionary<string, TenantChannel>(StringComparer.Ordinal)
            : (await db.ConversationSessions
                    .AsNoTracking()
                    .Where(s => s.TenantId == tenantId && sessionIds.Contains(s.SessionId))
                    .Select(s => new { s.SessionId, s.Channel })
                    .ToListAsync(ct))
                .GroupBy(s => s.SessionId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Channel, StringComparer.Ordinal);

        var channels = new Dictionary<TenantChannel, int>();
        var services = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var operation in operations)
        {
            if (!revisionByKey.TryGetValue($"{operation.IntentId}#{operation.IntentRevision}", out var revision))
                continue;

            if (!string.IsNullOrWhiteSpace(revision.SourceSessionId)
                && channelBySession.TryGetValue(revision.SourceSessionId, out var channel))
            {
                channels[channel] = channels.GetValueOrDefault(channel) + 1;
            }

            var serviceId = CanonicalBookingPayload.Parse(revision.CanonicalPayload)?.ServiceId;
            if (!string.IsNullOrWhiteSpace(serviceId))
                services[serviceId] = services.GetValueOrDefault(serviceId) + 1;
        }

        return new BookingOverview(
            tenantId,
            from,
            to,
            breakdownTruncated,
            perDay,
            channels.Select(kv => new OverviewChannel(kv.Key, kv.Value))
                .OrderByDescending(c => c.Count)
                .ToList(),
            services.Select(kv => new OverviewService(kv.Key, kv.Value))
                .OrderByDescending(s => s.Count)
                .ThenBy(s => s.ServiceId, StringComparer.Ordinal)
                .ToList());
    }

    /// <summary>
    /// A row parsed once: the payload plus the booking it refers to. The booking id can
    /// live on the operation row or inside the canonical payload depending on how far the
    /// workflow has progressed, so it is resolved once here and reused, rather than each
    /// call site picking a different one.
    /// </summary>
    /// <param name="BookingId">
    /// The authoritative booking, or <c>null</c> while the operation is still in flight —
    /// <c>BookingOperation.BookingId</c> is not assigned until finalization writes the provider
    /// reference, and a create's canonical payload carries no booking id at all.
    /// </param>
    /// <param name="Key">
    /// Identity for grouping and de-duplication. Falls back to the operation id, which is the only
    /// stable handle an unfinalised operation has. Never null, so it is safe as a dictionary key.
    /// </param>
    private sealed record ParsedRow(
        PayloadRow Row,
        CanonicalBookingPayload Payload,
        string? BookingId,
        string Key);

    private static List<ParsedRow> Parse(IEnumerable<PayloadRow> rows)
    {
        var parsed = new List<ParsedRow>();
        foreach (var row in rows)
        {
            var payload = CanonicalBookingPayload.Parse(row.CanonicalPayload);
            if (payload is null) continue;

            // NOT a filter. Every one of accepted/validating/reserving_capacity/held/
            // awaiting_payment/finalizing has no booking id yet, and dropping them told the
            // operator a slot was free while a guest already held it — a double-booking.
            var bookingId = FirstNonEmpty(payload.TargetBookingId, row.BookingId, payload.BookingId);

            parsed.Add(new ParsedRow(row, payload, bookingId, bookingId ?? row.OperationId));
        }
        return parsed;
    }

    /// <summary>
    /// Resolves the customer behind each operation.
    ///
    /// Deliberately NOT read from the canonical payload: <c>BookingRequest</c> has no
    /// customer field at all (and is <c>additionalProperties: false</c>), so a payload can
    /// never carry one. The authoritative identity is <c>bookings.customer_id</c> once the
    /// booking materialises, and before that the confirmation's <c>identity_ref</c> — the
    /// evidence of who confirmed the intent (spec 04 §8.8).
    /// </summary>
    private async Task<Func<ParsedRow, string?>> BuildCustomerResolverAsync(
        string tenantId,
        IReadOnlyCollection<ParsedRow> rows,
        CancellationToken ct)
    {
        var bookingIds = rows
            .Select(r => r.BookingId)
            .Where(id => id is not null)
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var customerByBooking = bookingIds.Length == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : (await db.Bookings
                    .AsNoTracking()
                    .Where(b => b.TenantId == tenantId && bookingIds.Contains(b.BookingId))
                    .Select(b => new { b.BookingId, b.CustomerId })
                    .ToListAsync(ct))
                .Where(b => !string.IsNullOrWhiteSpace(b.CustomerId))
                .GroupBy(b => b.BookingId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().CustomerId, StringComparer.Ordinal);

        var intentIds = rows.Select(r => r.Row.IntentId).Distinct(StringComparer.Ordinal).ToArray();

        var identityByIntent = intentIds.Length == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : (await db.IntentConfirmations
                    .AsNoTracking()
                    .Where(c => c.TenantId == tenantId && intentIds.Contains(c.IntentId))
                    .Select(c => new { c.IntentId, c.IntentRevision, c.IdentityRef })
                    .ToListAsync(ct))
                .Where(c => !string.IsNullOrWhiteSpace(c.IdentityRef))
                .GroupBy(c => $"{c.IntentId}#{c.IntentRevision}", StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().IdentityRef, StringComparer.Ordinal);

        return parsed =>
        {
            // An in-flight operation has no booking row yet; the confirmation's identity_ref below
            // is the evidence of who confirmed the intent (spec 04 §8.8) and covers that gap.
            if (parsed.BookingId is not null
                && customerByBooking.TryGetValue(parsed.BookingId, out var customerId))
                return customerId;

            var intentKey = $"{parsed.Row.IntentId}#{parsed.Row.IntentRevision}";
            return identityByIntent.TryGetValue(intentKey, out var identityRef) ? identityRef : null;
        };
    }

    /// <summary>
    /// Operations joined to their confirmed revision, newest activity first — the same
    /// shape <see cref="BookingReadRepository"/> reads, so the two stay consistent.
    /// </summary>
    private IQueryable<PayloadRow> PayloadRows(string tenantId)
        => from op in db.BookingOperations.AsNoTracking()
           join rev in db.BookingIntentRevisions.AsNoTracking()
               on new { op.TenantId, op.IntentId, Revision = op.IntentRevision }
               equals new { rev.TenantId, rev.IntentId, rev.Revision }
           where op.TenantId == tenantId
           // OperationId breaks ties: Skip/Take paging over a non-unique ORDER BY lets rows
           // with equal UpdatedAt be skipped or returned twice between pages.
           orderby op.UpdatedAt descending, op.OperationId
           select new PayloadRow
           {
               OperationId = op.OperationId,
               BookingId = op.BookingId,
               IntentId = op.IntentId,
               IntentRevision = op.IntentRevision,
               OperationType = op.OperationType,
               Status = op.Status,
               CreatedAt = op.CreatedAt,
               UpdatedAt = op.UpdatedAt,
               CanonicalPayload = rev.CanonicalPayload,
           };

    /// <summary>
    /// Builds the grid row. <paramref name="parsed"/> supplies the slot (the newest row that
    /// actually carries a start time); <paramref name="state"/> supplies the lifecycle — they
    /// differ when the newest activity on a booking is a cancellation or a modification, whose
    /// payloads carry no schedule of their own.
    /// </summary>
    private static ScheduledBooking ToScheduledBooking(
        ParsedRow parsed,
        string? customerRef,
        ParsedRow state)
    {
        var (row, payload, bookingId, _) = parsed;
        var stateRow = state.Row;

        return new ScheduledBooking(
            bookingId,
            row.OperationId,
            payload.BookingReference,
            payload.StartsAt,
            payload.PartySize,
            payload.DurationMinutes,
            payload.ServiceId,
            customerRef,
            // No booking row ⇒ no lifecycle. Every value of BookingLifecycleStatus
            // (confirmed/modification_pending/cancellation_pending/cancelled) presupposes a booking
            // exists, so the old `_ => "confirmed"` default reported an unfinalised create as
            // CONFIRMED — claiming an outcome the platform had not reached (Inv-16). It was
            // unreachable while in-flight rows were dropped; keeping them makes it live.
            state.BookingId is null ? null : ToLifecycleStatus(stateRow.OperationType, stateRow.Status),
            stateRow.Status,
            stateRow.OperationType,
            stateRow.UpdatedAt);
    }

    /// <summary>
    /// Mirrors <see cref="BookingReadRepository"/>'s mapping so the console and the
    /// manage-booking projection never disagree about a booking's lifecycle state.
    /// </summary>
    private static string ToLifecycleStatus(OperationTypeEnum operationType, OperationStatus status)
        => status switch
        {
            // A cancel has no `cancelled` operation status of its own (D1): it terminates at
            // `Booked` — "this operation succeeded" — with the BOOKING carrying the
            // cancellation. This arm MUST precede the generic `Booked` one, or a successfully
            // cancelled booking reads back as "confirmed".
            OperationStatus.Booked when operationType is OperationTypeEnum.Cancel => "cancelled",
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

    private sealed class PayloadRow
    {
        public required string OperationId { get; init; }

        public string? BookingId { get; init; }

        public required string IntentId { get; init; }

        public int IntentRevision { get; init; }

        public OperationTypeEnum OperationType { get; init; }

        public OperationStatus Status { get; init; }

        public DateTimeOffset CreatedAt { get; init; }

        public DateTimeOffset UpdatedAt { get; init; }

        public required string CanonicalPayload { get; init; }
    }
}
