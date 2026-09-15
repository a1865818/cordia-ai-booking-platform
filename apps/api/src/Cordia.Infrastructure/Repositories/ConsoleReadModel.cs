using Cordia.Domain.Console;
using Cordia.Domain.Bookings;
using Cordia.Domain.Entities;
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

        var resolveIdentity = await BuildCustomerResolverAsync(tenantId, matches, ct);
        var channelBySession = await LoadChannelsAsync(tenantId, matches, ct);
        var depositByOperation = await LoadDepositsAsync(tenantId, matches, ct);

        return new ScheduleResult(
            matches
                .Select(row => ToScheduledBooking(
                    row,
                    resolveIdentity(row),
                    latestState.TryGetValue(row.Key, out var latest) ? latest : row,
                    channelBySession,
                    depositByOperation))
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
        // Keyset cursor (#110): the (updated_at, operation_id) of the last row handed out. Offset
        // paging made page N produce and discard N×PageSize rows, and with no index behind the
        // ORDER BY every page re-sorted the tenant's entire operation set — quadratic in pages.
        // Resuming AFTER the last row reads each page as one range of the
        // (tenant_id, updated_at DESC, operation_id DESC) index. The cursor is the full sort key, so
        // rows sharing an updated_at are neither skipped nor repeated at a page boundary.
        PayloadRow? last = null;

        while (scanned < MaxScan)
        {
            var page = await After(PayloadRows(tenantId), last).Take(PageSize).ToListAsync(ct);
            if (page.Count == 0) return false; // tenant exhausted — the result is complete

            scanned += page.Count;
            last = page[^1];

            foreach (var parsed in Parse(page))
            {
                if (accept(parsed)) return false; // satisfied the caller's limit
            }

            if (page.Count < PageSize) return false; // last page — exhausted
        }

        // Reaching the ceiling is NOT the same as leaving rows unexamined, and reporting it as
        // such was a lie in the operator's direction that matters most. A tenant with exactly
        // MaxScan rows — or whose final page lands precisely on the boundary — has been read in
        // full, yet `return true` told the schedule its grid was partial and told the customers
        // search it may have missed someone who does not exist. "Possibly incomplete" is the one
        // answer an operator cannot act on, so it must not be given when the scan is provably done.
        //
        // One row is probed, and only in the rare ceiling case, so the ordinary path pays nothing.
        // `PayloadRows` orders by (updated_at desc, operation_id) — the same total order the paging
        // used — so "is there anything after the last row read" is well defined rather than planner luck.
        return await After(PayloadRows(tenantId), last).AnyAsync(ct);
    }

    /// <summary>
    /// Rows strictly after <paramref name="last"/> in the scan order
    /// <c>(updated_at DESC, operation_id DESC)</c>; all rows when <paramref name="last"/> is null.
    ///
    /// <para>Written as the row-value comparison <c>(updated_at, operation_id) &lt; (@u, @o)</c>.
    /// The equivalent <c>updated_at &lt; @u OR (updated_at = @u AND …)</c> returns the same rows but
    /// PostgreSQL can only apply it as a filter over an index walk from the top; a row comparison in
    /// the index's own column order is a seek, so every page costs the same wherever it starts.</para>
    /// </summary>
    private static IQueryable<PayloadRow> After(IQueryable<PayloadRow> rows, PayloadRow? last)
    {
        if (last is null)
            return rows;

        var updatedAt = last.UpdatedAt;
        var operationId = last.OperationId;
        return rows.Where(r => EF.Functions.LessThan(
            ValueTuple.Create(r.UpdatedAt, r.OperationId),
            ValueTuple.Create(updatedAt, operationId)));
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
                // Masked: this read is tenant-scoped but carries no grant, actor or reason code,
                // and `customer_ref` is a real address for any agent-created session.
                ContactMinimisation.Mask(s.CustomerRef),
                s.ConsentState,
                s.RecordingAllowed,
                s.StartedAt,
                s.EndedAt,
                s.Preview,
                // Sessions are not attributed; do not invent agent vs staff from the customer_ref.
                null))
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
                    // Masked. `NotificationRecipientResolver.AsDeliverable` reads this exact column
                    // as the SMS/email destination, so it is an address by construction.
                    ContactMinimisation.Mask(e.CustomerRef),
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

        var resolveIdentity = await BuildCustomerResolverAsync(tenantId, all, ct);

        // The customers projection wants only the identity half; the reference is a schedule concern.
        var withCustomer = all
            .Select(p => (Parsed: p, Identity: resolveIdentity(p)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Identity.CustomerRef))
            .ToList();

        // Matched against the MASKED value, not the raw one.
        //
        // A substring match over the raw column made this endpoint a phone-prefix oracle: `?query=`
        // of `+614` returned every guest whose number starts with it, on a read that needs nothing
        // but tenant scope. The console never uses the parameter (`useCustomers("", 50)` is its only
        // call site), so matching the masked form costs no working behaviour and removes the probe.
        var filtered = string.IsNullOrWhiteSpace(query)
            ? withCustomer
            : withCustomer
                .Where(x => ContactMinimisation.Mask(x.Identity.CustomerRef)!
                    .Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var page = filtered
            .GroupBy(x => x.Identity.CustomerRef!, StringComparer.Ordinal)
            .Select(ToCustomerAggregate)
            .OrderByDescending(a => a.Summary.LastSeenAt)
            .Take(limit)
            .ToList();

        var notes = await LoadNotesAsync(tenantId, page.Select(a => a.RawRef), ct);

        return new CustomerListResult(
            page.Select(a => AttachNotes(a.Summary, a.RawRef, notes)).ToList(),
            truncated);
    }

    public async Task<CustomerSummary?> GetCustomerAsync(
        string tenantId,
        string customerRef,
        CancellationToken ct = default)
    {
        var all = new List<ParsedRow>();
        await ScanAsync(tenantId, parsed => { all.Add(parsed); return false; }, ct);

        var resolveIdentity = await BuildCustomerResolverAsync(tenantId, all, ct);

        var withCustomer = all
            .Select(p => (Parsed: p, Identity: resolveIdentity(p)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Identity.CustomerRef))
            .ToList();

        var group = withCustomer
            .GroupBy(x => x.Identity.CustomerRef!, StringComparer.Ordinal)
            .FirstOrDefault(g =>
                string.Equals(g.Key, customerRef, StringComparison.Ordinal)
                || string.Equals(ContactMinimisation.Mask(g.Key), customerRef, StringComparison.Ordinal));

        var noteKeys = group is null ? new[] { customerRef } : new[] { group.Key, customerRef };
        var notes = await LoadNotesAsync(tenantId, noteKeys, ct);

        if (group is null)
        {
            if (!notes.ContainsKey(customerRef)) return null;
            return AttachNotes(
                new CustomerSummary(
                    ContactMinimisation.Mask(customerRef)!,
                    0, null, null, null, null,
                    Origin: null,
                    NoShowCount: 0,
                    Tags: [],
                    Note: null),
                customerRef,
                notes);
        }

        return AttachNotes(ToCustomerAggregate(group).Summary, group.Key, notes);
    }

    public async Task<KpiFacts> GetKpiFactsAsync(
        string tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        var bookedOps = await db.BookingOperations
            .AsNoTracking()
            .Where(o => o.TenantId == tenantId
                        && o.CreatedAt >= from
                        && o.CreatedAt < to
                        && o.Status == OperationStatus.Booked
                        && o.OperationType != OperationTypeEnum.Cancel)
            .Select(o => new { o.BookingId, o.AttributedSource })
            .ToListAsync(ct);

        var bookingIds = bookedOps
            .Select(o => o.BookingId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var bookingRows = bookingIds.Length == 0
            ? []
            : await db.Bookings
                .AsNoTracking()
                .Where(b => b.TenantId == tenantId && bookingIds.Contains(b.BookingId))
                .Select(b => new { b.BookingId, b.AttributedSource, b.Attendance })
                .ToListAsync(ct);

        var bookingById = bookingRows
            .GroupBy(b => b.BookingId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var knownAttendance = 0;
        var noShow = 0;
        var attributed = 0;
        var agent = 0;
        var operatorCount = 0;

        foreach (var op in bookedOps)
        {
            var booking = op.BookingId is not null
                          && bookingById.TryGetValue(op.BookingId, out var found)
                ? found
                : null;

            if (booking is not null)
            {
                if (booking.Attendance == AttendanceStatus.NoShow)
                {
                    knownAttendance++;
                    noShow++;
                }
                else if (booking.Attendance == AttendanceStatus.Attended)
                {
                    knownAttendance++;
                }
            }

            var source = booking?.AttributedSource ?? op.AttributedSource;
            if (source == AttributedSource.Agent)
            {
                attributed++;
                agent++;
            }
            else if (source == AttributedSource.Operator)
            {
                attributed++;
                operatorCount++;
            }
        }

        var paidMinor = await db.DepositSessions
            .AsNoTracking()
            .Where(d => d.TenantId == tenantId
                        && d.Status == DepositStatus.Paid
                        && (d.PaidAt ?? d.CreatedAt) >= from
                        && (d.PaidAt ?? d.CreatedAt) < to)
            .SumAsync(d => (long)d.AmountMinor, ct);

        return new KpiFacts(
            bookedOps.Count,
            knownAttendance,
            noShow,
            attributed,
            agent,
            operatorCount,
            paidMinor);
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
                // A successful CANCELLATION also settles as `Booked` — D1 gives cancel no terminal
                // state of its own — so counting it here made every cancellation look like a new
                // booking and inflated the chart the moment an operator cancelled one.
                // BookingFunnelReadModel already excludes it; this aggregate did not.
                Booked = g.Count(o => o.Status == OperationStatus.Booked
                    && o.OperationType != OperationTypeEnum.Cancel),
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
    private async Task<Func<ParsedRow, BookingIdentity>> BuildCustomerResolverAsync(
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

        // The reference rides along with the customer id rather than getting its own query or a
        // join in PayloadRows: it has to be keyed on the SAME booking id the row actually displays.
        // That id is the parsed one, which falls back to the payload when the operation row has no
        // BookingId yet — so a join on `booking_operations.booking_id` would miss exactly the rows
        // this is meant to fix.
        var rowByBooking = bookingIds.Length == 0
            ? new Dictionary<string, BookingRow>(StringComparer.Ordinal)
            : (await db.Bookings
                    .AsNoTracking()
                    .Where(b => b.TenantId == tenantId && bookingIds.Contains(b.BookingId))
                    .Select(b => new BookingRow(
                        b.BookingId, b.CustomerId, b.BookingReference, b.AttributedSource, b.Attendance))
                    .ToListAsync(ct))
                .GroupBy(b => b.BookingId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

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
            var booking = parsed.BookingId is not null
                          && rowByBooking.TryGetValue(parsed.BookingId, out var found)
                ? found
                : null;

            // The reference exists only once the booking materialises; before that it is genuinely
            // unknown and is reported as null rather than invented (Inv-16).
            var reference = string.IsNullOrWhiteSpace(booking?.BookingReference)
                ? null
                : booking!.BookingReference;

            // An in-flight operation has no booking row yet; the confirmation's identity_ref below
            // is the evidence of who confirmed the intent (spec 04 §8.8) and covers that gap.
            if (!string.IsNullOrWhiteSpace(booking?.CustomerId))
                return new BookingIdentity(
                    booking!.CustomerId, reference, booking.AttributedSource, booking.Attendance);

            var intentKey = $"{parsed.Row.IntentId}#{parsed.Row.IntentRevision}";
            return new BookingIdentity(
                identityByIntent.TryGetValue(intentKey, out var identityRef) ? identityRef : null,
                reference,
                booking?.AttributedSource,
                booking?.Attendance);
        };
    }

    /// <summary>What the <c>bookings</c> row contributes to a console projection.</summary>
    private sealed record BookingRow(
        string BookingId,
        string? CustomerId,
        string? BookingReference,
        AttributedSource? AttributedSource,
        AttendanceStatus Attendance);

    /// <summary>
    /// The identity facts a console row shows. Resolved together because both come from the same
    /// <c>bookings</c> lookup, and because the reference must be keyed on the booking id the row
    /// displays — not on the operation row's own (often null) <c>BookingId</c>.
    /// </summary>
    private sealed record BookingIdentity(
        string? CustomerRef,
        string? BookingReference,
        AttributedSource? AttributedSource,
        AttendanceStatus? Attendance);

    /// <summary>
    /// Channel of the session that produced each matched revision. Missing or blank session ids
    /// simply do not contribute — the schedule still renders the row.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, TenantChannel>> LoadChannelsAsync(
        string tenantId,
        IReadOnlyCollection<ParsedRow> rows,
        CancellationToken ct)
    {
        var sessionIds = rows
            .Select(r => r.Row.SourceSessionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (sessionIds.Length == 0)
            return new Dictionary<string, TenantChannel>(StringComparer.Ordinal);

        return (await db.ConversationSessions
                .AsNoTracking()
                .Where(s => s.TenantId == tenantId && sessionIds.Contains(s.SessionId))
                .Select(s => new { s.SessionId, s.Channel })
                .ToListAsync(ct))
            .GroupBy(s => s.SessionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Channel, StringComparer.Ordinal);
    }

    /// <summary>
    /// Latest deposit per matched operation. Missing operations simply do not contribute.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, DepositHint>> LoadDepositsAsync(
        string tenantId,
        IReadOnlyCollection<ParsedRow> rows,
        CancellationToken ct)
    {
        var operationIds = rows
            .Select(r => r.Row.OperationId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (operationIds.Length == 0)
            return new Dictionary<string, DepositHint>(StringComparer.Ordinal);

        return (await db.DepositSessions
                .AsNoTracking()
                .Where(d => d.TenantId == tenantId && operationIds.Contains(d.OperationId))
                .Select(d => new { d.OperationId, d.AmountMinor, d.Status, d.CreatedAt })
                .ToListAsync(ct))
            .GroupBy(d => d.OperationId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var latest = g.OrderByDescending(x => x.CreatedAt).First();
                    return new DepositHint(latest.AmountMinor, latest.Status);
                },
                StringComparer.Ordinal);
    }

    private async Task<IReadOnlyDictionary<string, CustomerOperatorNotes>> LoadNotesAsync(
        string tenantId,
        IEnumerable<string> customerRefs,
        CancellationToken ct)
    {
        var keys = customerRefs
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .SelectMany(r => new[] { r, ContactMinimisation.Mask(r)! })
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (keys.Length == 0)
            return new Dictionary<string, CustomerOperatorNotes>(StringComparer.Ordinal);

        return (await db.CustomerOperatorNotes
                .AsNoTracking()
                .Where(n => n.TenantId == tenantId && keys.Contains(n.CustomerRef))
                .ToListAsync(ct))
            .GroupBy(n => n.CustomerRef, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    private sealed record CustomerAggregate(string RawRef, CustomerSummary Summary);

    private static CustomerAggregate ToCustomerAggregate(
        IGrouping<string, (ParsedRow Parsed, BookingIdentity Identity)> group)
    {
        var newest = group.First().Parsed;
        var finalizedSources = group
            .Where(x => x.Parsed.BookingId is not null)
            .Select(x => x.Identity.AttributedSource);
        var noShowCount = group
            .Where(x => x.Identity.Attendance == AttendanceStatus.NoShow)
            .Select(x => x.Parsed.Key)
            .Distinct(StringComparer.Ordinal)
            .Count();

        return new CustomerAggregate(
            group.Key,
            new CustomerSummary(
                ContactMinimisation.Mask(group.Key)!,
                group.Select(x => x.Parsed.Key).Distinct(StringComparer.Ordinal).Count(),
                group.Min(x => x.Parsed.Row.CreatedAt),
                group.Max(x => x.Parsed.Row.UpdatedAt),
                newest.Payload.ServiceId,
                newest.BookingId,
                CustomerOrigin.From(finalizedSources),
                noShowCount,
                [],
                null));
    }

    private static CustomerSummary AttachNotes(
        CustomerSummary summary,
        string rawRef,
        IReadOnlyDictionary<string, CustomerOperatorNotes> notes)
    {
        CustomerOperatorNotes? row = null;
        if (notes.TryGetValue(rawRef, out var byRaw))
            row = byRaw;
        else if (notes.TryGetValue(summary.CustomerRef, out var byMasked))
            row = byMasked;

        if (row is null) return summary;

        var tags = ParseTags(row.TagsJson);
        var note = string.IsNullOrEmpty(row.Note) ? null : row.Note;
        return summary with { Tags = tags, Note = note };
    }

    private static IReadOnlyList<string> ParseTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return [];
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private sealed record DepositHint(int AmountMinor, DepositStatus Status);

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
           // OperationId breaks ties, and is the second half of the keyset cursor (#110): paging
           // over a non-unique ORDER BY skips or repeats rows sharing an UpdatedAt. BOTH columns
           // descend so the cursor is a single row-value comparison that the
           // (tenant_id, updated_at DESC, operation_id DESC) index can seek to.
           orderby op.UpdatedAt descending, op.OperationId descending
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
               AttributedSource = op.AttributedSource,
               SourceSessionId = rev.SourceSessionId,
           };

    /// <summary>
    /// Builds the grid row. <paramref name="parsed"/> supplies the slot (the newest row that
    /// actually carries a start time); <paramref name="state"/> supplies the lifecycle — they
    /// differ when the newest activity on a booking is a cancellation or a modification, whose
    /// payloads carry no schedule of their own.
    /// </summary>
    private static ScheduledBooking ToScheduledBooking(
        ParsedRow parsed,
        BookingIdentity identity,
        ParsedRow state,
        IReadOnlyDictionary<string, TenantChannel> channelBySession,
        IReadOnlyDictionary<string, DepositHint> depositByOperation)
    {
        var (row, payload, bookingId, _) = parsed;
        var stateRow = state.Row;
        // Masked at the point of emission. The schedule grid is a tenant-scoped read with no
        // grant, actor or reason code, and `identity.CustomerRef` is `bookings.customer_id` —
        // which falls back through `caller_id → phone_e164 → email`, so it is a raw address for
        // any booking the agent lane created. The grid only ever renders it as a label.
        var customerRef = ContactMinimisation.Mask(identity.CustomerRef);
        var sessionId = string.IsNullOrWhiteSpace(row.SourceSessionId) ? null : row.SourceSessionId;
        var channel = sessionId is not null
                      && channelBySession.TryGetValue(sessionId, out var foundChannel)
            ? foundChannel
            : (TenantChannel?)null;

        int? depositAmountMinor = null;
        if (depositByOperation.TryGetValue(row.OperationId, out var deposit)
            && deposit.Status is DepositStatus.Paid or DepositStatus.Created or DepositStatus.LinkSent)
        {
            depositAmountMinor = deposit.AmountMinor;
        }

        return new ScheduledBooking(
            bookingId,
            row.OperationId,
            // The bookings row wins: it is where finalize writes the reference, and it is not a
            // field of BookingRequest (additionalProperties: false), so a current-schema payload
            // can never carry one — reading only the payload returned null for every normal
            // booking and the grid rendered those rows as literal "Guest". The payload spelling
            // survives as a fallback for historical rows that happened to carry one.
            //
            // Same PRECEDENCE as BookingReadRepository.ToProjection, but not the same KEY: that
            // one joins on `booking_operations.booking_id` alone, while this resolves on
            // FirstNonEmpty(target_booking_id, row.BookingId, payload.BookingId). So for an
            // operation whose row carries no BookingId but whose payload names a target, the
            // console can show a reference manage-booking will not. Narrow — the console's own
            // cancel/modify doors set BookingId at creation — and the console being the more
            // complete of the two is the right direction, but they are not identical.
            FirstNonEmpty(identity.BookingReference, payload.BookingReference),
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
            stateRow.UpdatedAt,
            // Booking row wins when it has a value; otherwise the operation stamp (in-flight creates).
            identity.AttributedSource ?? row.AttributedSource,
            identity.Attendance,
            payload.ResourceId,
            channel,
            sessionId,
            depositAmountMinor);
    }

    /// <summary>
    /// Mirrors <see cref="BookingReadRepository"/>'s mapping so the console and the
    /// manage-booking projection never disagree about a booking's lifecycle state.
    /// </summary>
    private static string ToLifecycleStatus(OperationTypeEnum operationType, OperationStatus status)
        => BookingLifecycleProjection.From(operationType, status);

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

        public AttributedSource? AttributedSource { get; init; }

        public string? SourceSessionId { get; init; }
    }
}
