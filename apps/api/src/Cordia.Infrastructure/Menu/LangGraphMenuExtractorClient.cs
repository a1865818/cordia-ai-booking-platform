using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Menu;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cordia.Infrastructure.Menu;

/// <summary>
/// The real <see cref="IIsolatedMenuExtractor"/>: resolves the artifact reference to text and
/// POSTs it to the isolated extraction agent's dedicated RPC route inside the LangGraph process
/// (spec 11 §3, issue #87). The agent runs with an empty tool allowlist and carries the document
/// as UNTRUSTED content; this class does nothing but move bytes across that seam and translate
/// failures.
///
/// <para><b>Fail closed, always.</b> Every failure category — unresolvable ref, transport error,
/// non-2xx, a response the wire contract refuses, a timeout — becomes an exception. None of them
/// may become an empty <see cref="MenuExtractionResult"/>, which the ingestion handler would settle
/// as <c>DraftReady</c> with zero items and the owner would read as "your menu is empty".</para>
///
/// <para><b>Nothing derived from the response is ever put in an exception message or a log.</b>
/// The response body is a function of an untrusted uploaded document, and an error body from a
/// misbehaving agent (or a proxy in front of it) routinely quotes the request back. Only the
/// status code and exception TYPE NAMES cross into diagnostics (Inv-12 / Inv-16).</para>
/// </summary>
public sealed class LangGraphMenuExtractorClient : IIsolatedMenuExtractor
{
    /// <summary>
    /// Header the shared secret rides in (D-G). Mirrors <c>INTERNAL_KEY_HEADER</c> in
    /// <c>apps/langgraph/cordia_agent/menu/http_app.py</c> — change both or neither.
    /// </summary>
    internal const string InternalKeyHeader = "X-Cordia-Internal-Key";

    /// <summary><see cref="ISecretStore"/> key holding that secret; absent ⇒ header omitted.</summary>
    internal const string InternalKeySecretName = "LangGraphMenuExtractionKey";

    /// <summary>Correlation header, so a failure here can be joined to the route's own log line.</summary>
    internal const string CorrelationHeader = "X-Correlation-Id";

    // ---- Length bounds on model-produced text (see BoundedSanitize) --------------------------
    //
    // `vendor_catalog_items` declares NO `HasMaxLength` anywhere (VendorCatalogItemConfiguration),
    // so the columns are unbounded `text`/`jsonb` and these constants are the only bound that
    // exists on the path from an attacker-supplied document to `SaveChanges`.

    /// <summary>
    /// Tightest bound of the set, because <c>item_id</c> is half a btree key: the PK is
    /// <c>(tenant_id, item_id)</c> and PostgreSQL cannot index a tuple over 2704 bytes
    /// (<c>54000 index row size … exceeds btree version 4 maximum</c>). The ingestion handler
    /// stores <c>"{CatalogId}:{ItemId}"</c>, so the budget must cover that prefix and the tenant
    /// id too. 128 chars is at most 512 UTF-8 bytes — comfortably inside the limit even with both
    /// — and still far more than the agent's own <c>item_{n}</c> / slug ids ever need.
    /// </summary>
    internal const int MaxItemIdChars = 128;

    internal const int MaxNameChars = 512;
    internal const int MaxDescriptionChars = 4000;
    internal const int MaxCategoryChars = 128;

    /// <summary>ISO-4217 is 3 characters; the slack is for bounding, not for validating.</summary>
    internal const int MaxCurrencyChars = 16;

    internal const int MaxSourceLocationChars = 512;
    internal const int MaxImageRefChars = 1024;

    /// <summary>Matches Phase 3's <c>MaxAuditedFlagFieldChars</c>, where these same labels land.</summary>
    internal const int MaxFlagReasonChars = 200;

    /// <summary>Cap on how many modifiers one item may carry into the jsonb column.</summary>
    internal const int MaxModifiers = 50;

    /// <summary>
    /// Backstop on a serialized jsonb column. Over it the value is DROPPED to <c>null</c>, never
    /// truncated: a truncated JSON document is invalid JSON, and handing that to a <c>jsonb</c>
    /// column throws at <c>SaveChanges</c> — the exact poison loop the bounds exist to prevent.
    /// With per-leaf bounds and <see cref="MaxModifiers"/> already applied this should never fire.
    /// </summary>
    internal const int MaxJsonColumnChars = 64 * 1024;

    /// <summary>
    /// Above this, <c>artifact_ref</c> is omitted from the request rather than sent.
    ///
    /// <para>Two independent reasons, either sufficient. (1) The canonical ref today is
    /// <c>inline:&lt;the entire document&gt;</c>, so sending it verbatim puts the menu in the body
    /// TWICE, and the route's <c>MAX_REQUEST_BODY_BYTES</c> (<c>4 ×</c> the text budget) is sized to
    /// absorb JSON escaping on ONE copy, not to carry a second one — an at-cap document plus its own
    /// ref spends over half the transport guard on a field the route only ever reduces to a digest.
    /// (2) The ref is a <c>SensitiveDataMask</c>
    /// control-plane key this codebase refuses to log raw; the route would reduce it to a digest
    /// anyway. Correlation rides <see cref="CorrelationHeader"/> instead, which is what the route
    /// can actually join on.</para>
    /// </summary>
    internal const int MaxArtifactRefChars = 256;

    private readonly HttpClient _http;
    private readonly IMenuArtifactTextResolver _resolver;
    private readonly ISecretStore _secrets;
    private readonly ICorrelationContext _correlation;
    private readonly LangGraphOptions _options;
    private readonly ILogger<LangGraphMenuExtractorClient> _logger;

    public LangGraphMenuExtractorClient(
        HttpClient http,
        IMenuArtifactTextResolver resolver,
        ISecretStore secrets,
        ICorrelationContext correlation,
        IOptions<LangGraphOptions> options,
        ILogger<LangGraphMenuExtractorClient> logger)
    {
        _http = http;
        _resolver = resolver;
        _secrets = secrets;
        _correlation = correlation;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MenuExtractionResult> ExtractAsync(string artifactRef, CancellationToken ct = default)
    {
        // The client owns its own deadline rather than trusting the caller to have one. The call
        // happens inside an open inbox transaction, so an unbounded wait is an unbounded
        // transaction hold. HttpClient.Timeout is set to the same value on the typed client;
        // whichever fires first lands in the same catch below.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.ExtractTimeoutSeconds));

        try
        {
            // INSIDE the try, and deliberately so. The resolver is pure and synchronous today, but
            // the port exists precisely so #84 can put object storage behind it — network I/O with
            // its own HttpClient and its own timeout. On that day an S3 timeout would raise
            // TaskCanceledException with the caller's token uncancelled, and if the call sat
            // outside this try it would sail past the D-F translation below, past the ingestion
            // handler's fail-secure catch, and poison-loop the message (Inv-5). The deadline is
            // also honestly the RESOLVER's to share: the transaction is open for its I/O too.
            var menuText = await _resolver.ResolveTextAsync(artifactRef, timeoutCts.Token);

            using var response = await SendAsync(menuText, artifactRef, timeoutCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                // The STATUS CODE only. The route's own error bodies are a static
                // {"error": "<category>"} by design, but this client must not depend on that being
                // true of whatever actually answered — a proxy, a dev server, or a drifted route
                // can put anything here, including a reflection of the menu text.
                _logger.LogWarning(
                    "Menu extraction agent answered {StatusCode}; settling the ingestion as failed.",
                    (int)response.StatusCode);
                throw new MenuExtractionFailedException(
                    $"Menu extraction agent returned HTTP {(int)response.StatusCode}.");
            }

            var payload = await response.Content.ReadFromJsonAsync<WireExtractionResponse>(
                MenuExtractionWire.Options, timeoutCts.Token);

            if (payload is null)
                throw new MenuExtractionFailedException("Menu extraction agent returned an empty response body.");

            return Map(payload);
        }
        catch (MenuArtifactUnresolvableException)
        {
            // Ahead of every catch below, so moving the resolver inside the try changed nothing
            // about this outcome. It is already the right, distinct answer ("we could not read
            // your upload") and the ingestion handler settles it to ExtractFailed on its own;
            // re-wrapping it would blame the agent for something that never reached it.
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // D-F, AND THE ORDER OF THE CHECK IS THE WHOLE POINT.
            //
            // An HttpClient timeout throws TaskCanceledException : OperationCanceledException even
            // though the CALLER never cancelled anything. IngestVendorMenuCommandHandler's
            // fail-secure catch is `catch (Exception ex) when (ex is not OperationCanceledException)`
            // — deliberately, so a real shutdown/cancel aborts without settling the row. An
            // untranslated timeout would slip straight through that guard, roll the inbox
            // transaction back, un-admit the message and have it redelivered forever, re-extracting
            // the same (possibly hostile, certainly expensive) document on every loop (Inv-5).
            //
            // The `when (!ct.IsCancellationRequested)` filter is what keeps genuine caller
            // cancellation intact: if the caller's token IS cancelled, this does not match and the
            // OperationCanceledException propagates untouched.
            _logger.LogWarning(
                "Menu extraction timed out after {TimeoutSeconds}s; settling the ingestion as failed.",
                _options.ExtractTimeoutSeconds);
            throw new MenuExtractionFailedException(
                $"Menu extraction timed out after {_options.ExtractTimeoutSeconds}s.");
        }
        catch (JsonException)
        {
            // The exception is NOT chained and its message is NOT logged. An unmapped-member
            // JsonException names the offending property — attacker-influenced text — and a
            // conversion failure can quote the value. The type name is all diagnostics get.
            _logger.LogWarning(
                "Menu extraction response was refused by the wire contract (unexpected shape or field).");
            throw new MenuExtractionFailedException(
                "Menu extraction agent returned a response the wire contract refuses.");
        }
        catch (NotSupportedException)
        {
            // ReadFromJsonAsync refuses a non-JSON media type. A 200 that is not JSON is as much a
            // contract violation as a 200 whose JSON has an extra field.
            _logger.LogWarning("Menu extraction response was not JSON; settling the ingestion as failed.");
            throw new MenuExtractionFailedException(
                "Menu extraction agent returned a response the wire contract refuses.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                "Menu extraction transport failed ({ExceptionType}); settling the ingestion as failed.",
                ex.GetType().Name);
            throw new MenuExtractionFailedException("Menu extraction agent was unreachable.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string menuText, string artifactRef, CancellationToken ct)
    {
        // Omitted when long — see MaxArtifactRefChars. The `inline:` ref IS the document, and
        // sending it would both double the body (busting the route's budget) and put untrusted
        // content in a field this codebase refuses to log. A .NET-side hash would be no better:
        // the route reduces whatever it receives through its own `_artifact_log_token`, so a
        // digest would just get digested again and correlate with nothing.
        var loggableRef = artifactRef.Length <= MaxArtifactRefChars ? artifactRef : null;

        var body = JsonSerializer.Serialize(
            new WireExtractRequest(menuText, loggableRef), MenuExtractionWire.Options);

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.ExtractPath)
        {
            // application/json is REQUIRED by the route, which answers 415 otherwise: text/plain
            // and the form encodings are CORS "simple requests", so demanding JSON forces a
            // preflight that a drive-by page cannot satisfy.
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        // Sent only when configured — the route fails open when CORDIA_MENU_EXTRACTION_API_KEY is
        // unset, which is the dev default on an unauthenticated :2024. ASCII-only by contract
        // (documented in apps/langgraph/.env.example): the route decodes headers as latin-1 and
        // compares UTF-8, and the two agree exactly on ASCII. Never logged, here or anywhere.
        //
        // TRIMMED, and whitespace-only treated as absent, because the route reads the expected
        // value as `os.environ.get(...).strip()`. A secret configured as "  k  " would otherwise
        // be sent verbatim, fail the constant-time compare against "k", and 401 forever — a
        // misconfiguration with no plausible diagnosis from either side of the wire.
        var key = _secrets.GetSecret(InternalKeySecretName);
        if (!string.IsNullOrWhiteSpace(key))
            request.Headers.TryAddWithoutValidation(InternalKeyHeader, key.Trim());

        // Free (Starlette ignores unknown headers) and the only remaining way to join this
        // client's "HTTP 502" line to the route's own log line now that artifact_ref is usually
        // omitted. On a background lane with no operator watching, that join is the difference
        // between a diagnosable failure and a mystery.
        var correlationId = _correlation.Current.CorrelationId;
        if (!string.IsNullOrWhiteSpace(correlationId))
            request.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId);

        // Awaited here rather than returning the Task: `request` is disposed on the way out of
        // this method, and handing the task back would dispose it mid-flight.
        return await _http.SendAsync(request, ct);
    }

    /// <summary>
    /// Maps the wire response onto the Domain result. Absent required members are a contract
    /// violation, not a default: an item with no price or no name is not a draft an owner can
    /// review, and inventing zeroes for it would put a fabricated $0.00 row in the catalog.
    /// </summary>
    private MenuExtractionResult Map(WireExtractionResponse payload)
    {
        // A NULL or ABSENT `items`/`flags` is a contract violation, not an empty extraction.
        //
        // `JsonUnmappedMemberHandling.Disallow` says nothing about a member that is present-and-null
        // or missing entirely, so `{"items":null,"flags":null}`, `{"flags":[]}` and even `{}` all
        // deserialized happily and mapped to MenuExtractionResult(0, 0) — accepted as SUCCESS, which
        // settles the catalog to DraftReady with zero items and reads to the owner as "your menu is
        // empty". That is fail-OPEN in a class whose contract is "fail closed, always".
        //
        // The real route cannot produce it: `ExtractionResult.items` is required, `flags` has a
        // default_factory, and the envelope is round-tripped through
        // model_validate(...).model_dump(mode="json"). Only a NON-CONFORMING responder — a reverse
        // proxy, a drifted route, a dev server — answers this shape, which is exactly the threat
        // model this class documents.
        //
        // An empty-but-PRESENT `items: []` is deliberately NOT touched: a readable document with no
        // priced rows is a legitimate extraction outcome on both sides of the wire (http_app.py, and
        // model_azure.py's "rows reported but none survived → raise" vs "model reports zero rows →
        // []"), and failing it would mislabel a readable-but-itemless menu as "artifact unreadable".
        if (payload.Items is null || payload.Flags is null)
        {
            _logger.LogWarning(
                "Menu extraction response omitted items or flags; settling the ingestion as failed.");
            throw new MenuExtractionFailedException(
                "Menu extraction agent returned a response with no items or flags member.");
        }

        var items = payload.Items.Select(MapItem).ToList();
        var flags = payload.Flags
            .Select(f => new MenuExtractionFlag(
                Sanitize(f.ItemId, MaxItemIdChars),
                Sanitize(f.Reason, MaxFlagReasonChars) ?? string.Empty))
            .ToList();

        EnsureItemIdsAreDistinct(items);

        return new MenuExtractionResult(items, flags);
    }

    /// <summary>
    /// Refuses a response whose SANITIZED item ids collide — a poison loop, not a data-quality nit.
    ///
    /// <para><see cref="Sanitize"/> is not injective, and both of its collapsing steps are reachable
    /// from attacker-supplied document content: control-character removal maps <c>"SKU\tA1"</c> and
    /// <c>"SKUA1"</c> onto the same id, and truncation to <see cref="MaxItemIdChars"/> maps any two
    /// ids sharing their first 128 characters onto the same id. Python dedupes at FULL length on the
    /// raw string (<c>_unique_item_id</c>) and <c>ExtractedMenuItem.item_id</c> carries no
    /// <c>max_length</c>, so nothing upstream can prevent either.</para>
    ///
    /// <para><b>Why it has to be caught HERE.</b> Two drafts with the same
    /// <c>(tenant_id, item_id)</c> throw at <c>AddRange</c> inside
    /// <c>VendorCatalogRepository.AddDraftItemsAsync</c> — EF's identity map rejects the second
    /// instance before any SQL runs (<c>InvalidOperationException: The instance of entity type
    /// 'VendorCatalogItem' cannot be tracked because another instance with the same key value for
    /// {'TenantId', 'ItemId'} is already being tracked</c>) — and on a redelivery path PostgreSQL
    /// raises <c>duplicate key value violates unique constraint</c> at <c>SaveChanges</c>. Both land
    /// AFTER <c>IngestVendorMenuCommandHandler</c>'s fail-secure catch has closed, so the inbox
    /// transaction rolls back, the admission is un-done, and a paid LLM extraction re-runs on every
    /// redelivery (Inv-5).</para>
    ///
    /// <para><b>Throwing, not renaming.</b> Suffixing a collision would invent an id no owner and no
    /// document ever authorised, and silently split or merge rows in a draft catalogue the owner is
    /// about to approve. Failing closed matches this class's stated contract: the catalog settles
    /// <c>ExtractFailed</c>, the owner is alerted, and recovery is a fresh upload.</para>
    /// </summary>
    private void EnsureItemIdsAreDistinct(IReadOnlyList<DraftCatalogItem> items)
    {
        var seen = new HashSet<string>(items.Count, StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (seen.Add(item.ItemId))
                continue;

            // The COUNT only — never the colliding id, which is model-shaped text derived from an
            // untrusted document (Inv-12/16).
            _logger.LogWarning(
                "Menu extraction returned {Count} items with a duplicate item id after sanitization; "
                + "settling the ingestion as failed rather than letting the draft insert throw.",
                items.Count);
            throw new MenuExtractionFailedException(
                "Menu extraction agent returned two items whose ids are identical after sanitization.");
        }
    }

    private DraftCatalogItem MapItem(WireCatalogItem item)
    {
        if (item.ItemId is null || item.Name is null || item.Description is null
            || item.Price?.AmountMinor is null || item.Price.Currency is null)
        {
            throw new MenuExtractionFailedException(
                "Menu extraction agent returned an item missing a required field.");
        }

        return new DraftCatalogItem(
            ItemId: Sanitize(item.ItemId, MaxItemIdChars)!,
            Name: Sanitize(item.Name, MaxNameChars)!,
            Description: Sanitize(item.Description, MaxDescriptionChars)!,
            PriceMinor: item.Price.AmountMinor.Value,
            Currency: Sanitize(item.Price.Currency, MaxCurrencyChars)!,
            ExtractionConfidence: item.ExtractionConfidence,
            SourceLocation: Sanitize(item.SourceLocation, MaxSourceLocationChars),
            Category: Sanitize(item.Category, MaxCategoryChars),
            Modifiers: SerializeModifiers(item.Modifiers),
            AvailabilityWindow: SerializeAvailabilityWindow(item.AvailabilityWindow),
            ImageRef: Sanitize(item.ImageRef, MaxImageRefChars),
            // Unlike `allergens`/`dietary_labels`, this one HAS somewhere to land:
            // vendor_catalog_items.duration_minutes exists (VendorCatalogItemConfiguration, migration
            // 20260813121656_ConsoleCompleteness) and both customer-facing reads already select it
            // (SearchVendorCatalogQuery, GetCatalogItemQuery). Dropping it here made every extracted
            // duration permanently null with no way for the owner to repair it — CatalogItemEdit
            // exposes only ItemId/Name/Description/PriceMinor — so the foreground agent could never
            // learn how long a "60-minute massage" takes. Not bounded or sanitized: it is an int?,
            // which carries no control characters and no length.
            DurationMinutes: item.DurationMinutes);
    }

    /// <summary>
    /// Re-serializes the modifiers for the <c>modifiers</c> column, sanitizing the leaf strings
    /// <b>before</b> they are encoded rather than scrubbing the JSON afterwards.
    ///
    /// <para><b>Order matters and is load-bearing.</b> <c>vendor_catalog_items.modifiers</c> and
    /// <c>availability_window</c> are <c>jsonb</c>, and PostgreSQL rejects <c>U+0000</c> in jsonb
    /// (<c>22P05 unsupported Unicode escape sequence</c>) — including the escaped form. But
    /// <c>JsonSerializer</c> escapes control characters on the way out, so a NUL inside a modifier
    /// name leaves as the six ASCII characters <c>\u0000</c>, which a control-character filter run
    /// over the SERIALIZED text would not see at all. Stripping first is the only order that
    /// works.</para>
    ///
    /// <para>Phase 3 fixed the same hazard on the audit path (<c>SanitizeAuditLabel</c>) for the
    /// same reason: the throw would land at <c>SaveChanges</c>, outside the ingestion handler's
    /// fail-secure catch, rolling back the inbox transaction and poison-looping the message
    /// (Inv-5).</para>
    /// </summary>
    private string? SerializeModifiers(IReadOnlyList<WireCatalogModifier>? modifiers)
    {
        if (modifiers is null)
            return null;

        var sanitized = modifiers
            .Take(MaxModifiers)
            .Select(m => new WireCatalogModifier(
                Sanitize(m.ModifierId, MaxItemIdChars),
                Sanitize(m.Name, MaxNameChars),
                m.PriceDelta is null
                    ? null
                    : new WireMoney(m.PriceDelta.AmountMinor, Sanitize(m.PriceDelta.Currency, MaxCurrencyChars))))
            .ToList();

        if (modifiers.Count > MaxModifiers)
            _logger.LogWarning("Menu extraction returned {Count} modifiers for one item; keeping the first {Kept}.",
                modifiers.Count, MaxModifiers);

        return BoundJsonColumn(JsonSerializer.Serialize(sanitized, MenuExtractionWire.Options), "modifiers");
    }

    /// <summary>Same contract as <see cref="SerializeModifiers"/>, for the availability window.</summary>
    private string? SerializeAvailabilityWindow(WireAvailabilityWindow? window)
    {
        if (window is null)
            return null;

        var sanitized = new WireAvailabilityWindow(
            window.DaysOfWeek?.Take(MaxModifiers).Select(d => Sanitize(d, MaxCategoryChars) ?? string.Empty).ToList(),
            Sanitize(window.StartTime, MaxCategoryChars),
            Sanitize(window.EndTime, MaxCategoryChars));

        return BoundJsonColumn(
            JsonSerializer.Serialize(sanitized, MenuExtractionWire.Options), "availability_window");
    }

    /// <summary>
    /// Last-resort bound on a serialized <c>jsonb</c> column: over
    /// <see cref="MaxJsonColumnChars"/> the value is <b>dropped to null, never truncated</b>.
    ///
    /// <para>Truncating would be the actively worse choice. A cut-off JSON document is invalid
    /// JSON, and PostgreSQL parses on insert into a <c>jsonb</c> column — so the "fix" would throw
    /// at <c>SaveChanges</c>, outside the ingestion handler's fail-secure catch, which is the exact
    /// poison loop (Inv-5) these bounds exist to prevent. Losing advisory modifier data off a draft
    /// the owner is about to review is the cheap failure; wedging the queue is not.</para>
    /// </summary>
    private string? BoundJsonColumn(string json, string column)
    {
        if (json.Length <= MaxJsonColumnChars)
            return json;

        _logger.LogWarning(
            "Menu extraction produced an oversized {Column} document ({Length} chars); dropping it from the draft.",
            column, json.Length);
        return null;
    }

    /// <summary>
    /// Strips control characters from one model-produced string and bounds its length, on its way
    /// into a <c>vendor_catalog_items</c> column. Both halves guard the same failure mode.
    ///
    /// <para><b>Control characters.</b> PostgreSQL rejects <c>U+0000</c> in <c>text</c> as flatly
    /// as in <c>jsonb</c>. All control characters go, not just NUL: none has a legitimate meaning
    /// in an item name, a currency code or a source location, and the alternative is a
    /// per-character allowlist that would have to be right about every one of them.</para>
    ///
    /// <para><b>Length.</b> The columns declare no <c>HasMaxLength</c>, so nothing else bounds
    /// them — but <c>item_id</c> is half the <c>(tenant_id, item_id)</c> btree PK, and an
    /// oversized value fails with <c>54000 index row size … exceeds btree version 4 maximum
    /// 2704</c>. It bites unpredictably: index tuples are compressed, so a repetitive 3000-char id
    /// inserts fine while a high-entropy one of the same length (a base64 blob or a hash printed
    /// on a menu) does not — meaning it passes casual testing and fails on real hostile input.</para>
    ///
    /// <para><b>Both failures land outside the ingestion handler's fail-secure catch</b> — the
    /// oversized-index one at <c>SaveChanges</c>, after the handler has returned — so the inbox
    /// transaction rolls back, the message is redelivered, and a paid LLM extraction re-runs on
    /// every loop (Inv-5). Same reasoning, and the same remedy, as Phase 3's
    /// <c>SanitizeAuditLabel</c> on the audit path.</para>
    ///
    /// <para><b>Neither step is injective, and that is its own hazard.</b> Truncation and
    /// control-character removal can both collapse two distinct agent ids onto one, and duplicate
    /// <c>item_id</c>s throw EARLIER than <c>SaveChanges</c> — at <c>AddRange</c>, on EF's identity
    /// map — but still outside that same catch. <see cref="EnsureItemIdsAreDistinct"/> is what turns
    /// that into a <see cref="MenuExtractionFailedException"/> the handler can actually see.</para>
    /// </summary>
    private static string? Sanitize(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var cleaned = value.Any(char.IsControl)
            ? new string(value.Where(c => !char.IsControl(c)).ToArray())
            : value;

        if (cleaned.Length <= maxChars)
            return cleaned;

        // Never cut between a surrogate pair: the lone high surrogate left behind is not valid
        // text, and the JSON writer would have to substitute U+FFFD for it on the jsonb path.
        var end = maxChars;
        if (char.IsHighSurrogate(cleaned[end - 1]))
            end--;

        return cleaned[..end];
    }
}
