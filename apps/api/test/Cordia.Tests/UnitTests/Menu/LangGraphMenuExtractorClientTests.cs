using System.Net;
using System.Text.Json;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Menu;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Cordia.Tests.UnitTests.Menu;

/// <summary>
/// Unit tests for <see cref="LangGraphMenuExtractorClient"/> (issue #87 Phase 4), driven entirely
/// through <see cref="FakeHttpMessageHandler"/> — no socket is opened.
///
/// <para>What is actually under test is the seam's failure behaviour, not its happy path:</para>
/// <list type="bullet">
///   <item>the wire contract is CLOSED — an unexpected or tool-shaped field, at the envelope OR
///     nested inside an item, is a throw and never a silent drop;</item>
///   <item>a timeout becomes a <see cref="MenuExtractionFailedException"/> and NOT an
///     <see cref="OperationCanceledException"/> (D-F) — the load-bearing property, because the
///     ingestion handler's fail-secure catch deliberately re-throws the latter and the message
///     would poison-loop (Inv-5);</item>
///   <item>caller cancellation still propagates untouched, so a real shutdown is not mistaken for
///     a failed extraction;</item>
///   <item>nothing derived from the response body reaches an exception message (Inv-12/16);</item>
///   <item>control characters never leave the mapping boundary, because the columns they land in
///     are PostgreSQL <c>text</c> and <c>jsonb</c>, both of which reject <c>U+0000</c>.</item>
/// </list>
/// </summary>
[Trait("Category", "Unit")]
public class LangGraphMenuExtractorClientTests : IDisposable
{
    private const string MenuText = "Grilled Salmon - $32.00";
    private const string ArtifactRef = "inline:" + MenuText;

    /// <summary>A body that would be devastating in a log line or an exception message.</summary>
    private const string Canary = "canary-9f2b1c-do-not-leak";

    private const string MinimalOkResponse = """{"items":[],"flags":[]}""";

    // ---------------------------------------------------------------------
    // Happy path
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Extract_HappyPath_MapsItemsAndFlags()
    {
        var handler = FakeHttpMessageHandler.RespondingJson(
            """
            {
              "items": [
                {
                  "item_id": "item_1",
                  "name": "Grilled Salmon",
                  "description": "Served with seasonal greens.",
                  "price": {"amount_minor": 3200, "currency": "AUD"},
                  "duration_minutes": null,
                  "category": "Mains",
                  "modifiers": [
                    {"modifier_id": "mod_1", "name": "Extra lemon", "price_delta": {"amount_minor": 150, "currency": "AUD"}}
                  ],
                  "allergens": ["fish"],
                  "dietary_labels": ["gluten-free"],
                  "availability_window": {"days_of_week": ["mon"], "start_time": "17:00", "end_time": "21:00"},
                  "image_ref": "s3://img/1.png",
                  "extraction_confidence": 0.42,
                  "source_location": "page 2, Mains"
                }
              ],
              "flags": [{"item_id": "item_1", "reason": "low_confidence"}, {"item_id": null, "reason": "rows_dropped"}]
            }
            """);

        var result = await CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal("item_1", item.ItemId);
        Assert.Equal("Grilled Salmon", item.Name);
        Assert.Equal("Served with seasonal greens.", item.Description);
        Assert.Equal(3200, item.PriceMinor);
        Assert.Equal("AUD", item.Currency);
        Assert.Equal("Mains", item.Category);
        Assert.Equal("s3://img/1.png", item.ImageRef);
        Assert.Equal(0.42, item.ExtractionConfidence);
        Assert.Equal("page 2, Mains", item.SourceLocation);

        // The .NET columns are jsonb-backed strings, so the nested shapes are re-serialized.
        Assert.Equal(
            """[{"modifier_id":"mod_1","name":"Extra lemon","price_delta":{"amount_minor":150,"currency":"AUD"}}]""",
            item.Modifiers);
        Assert.Equal(
            """{"days_of_week":["mon"],"start_time":"17:00","end_time":"21:00"}""",
            item.AvailabilityWindow);

        Assert.Collection(
            result.Flags,
            f => { Assert.Equal("item_1", f.ItemId); Assert.Equal("low_confidence", f.Reason); },
            f => { Assert.Null(f.ItemId); Assert.Equal("rows_dropped", f.Reason); });
    }

    [Fact]
    public async Task Extract_PostsResolvedTextAndRefAsJson_ToTheExtractPath()
    {
        var handler = FakeHttpMessageHandler.RespondingJson(MinimalOkResponse);

        await CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("http://localhost:2024/internal/menu/extract", handler.LastRequestUri?.ToString());

        // The route answers 415 to anything else: text/plain and the form encodings are CORS
        // "simple requests", so demanding JSON is what forces a preflight a drive-by page cannot
        // satisfy. A charset parameter is allowed, the media type is not.
        Assert.Equal("application/json", handler.LastContentType);

        using var sent = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(MenuText, sent.RootElement.GetProperty("menu_text").GetString());
        Assert.Equal(ArtifactRef, sent.RootElement.GetProperty("artifact_ref").GetString());
        Assert.Equal(2, sent.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task Extract_EmptyEnvelope_IsASuccessfulEmptyExtraction()
    {
        // A blank document legitimately extracts to nothing. That must stay distinguishable from
        // a failure, which is why every failure path here throws instead of returning this.
        var result = await CreateClient(FakeHttpMessageHandler.RespondingJson(MinimalOkResponse))
            .ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken);

        Assert.Empty(result.Items);
        Assert.Empty(result.Flags);
    }

    [Fact]
    public async Task Extract_DurationMinutes_IsCarriedOntoTheDraft()
    {
        // NOT the `allergens`/`dietary_labels` "nowhere to land" case: `duration_minutes` has a
        // column (vendor_catalog_items.duration_minutes) and BOTH customer-facing reads already
        // select it (SearchVendorCatalogQuery, GetCatalogItemQuery). Dropped here, every extracted
        // duration was permanently null with no owner repair path — CatalogItemEdit exposes only
        // ItemId/Name/Description/PriceMinor — so the foreground agent could never learn how long a
        // "60-minute massage" takes.
        var handler = FakeHttpMessageHandler.RespondingJson(
            """
            {"items":[{"item_id":"svc_1","name":"Deep Tissue Massage","description":"60 minutes.",
              "price":{"amount_minor":12000,"currency":"AUD"},"duration_minutes":60}],"flags":[]}
            """);

        var result = await CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken);

        Assert.Equal(60, Assert.Single(result.Items).DurationMinutes);
    }

    [Fact]
    public async Task Extract_AbsentDurationMinutes_MapsToNull_NotZero()
    {
        // A duration the document never stated must stay unknown. Zero would be a fabricated fact
        // the storefront would render as a zero-length service (Inv-16).
        var handler = FakeHttpMessageHandler.RespondingJson(
            """{"items":[{"item_id":"i","name":"n","description":"d","price":{"amount_minor":1,"currency":"AUD"}}],"flags":[]}""");

        var result = await CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(result.Items).DurationMinutes);
    }

    // ---------------------------------------------------------------------
    // The wire contract is closed (D-C)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Extract_UnexpectedEnvelopeField_Throws()
    {
        var handler = FakeHttpMessageHandler.RespondingJson(
            """{"items":[],"flags":[],"total_count":3}""");

        await Assert.ThrowsAsync<MenuExtractionFailedException>(
            () => CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Extract_ToolShapedEnvelope_Throws()
    {
        var handler = FakeHttpMessageHandler.RespondingJson(
            """{"items":[],"flags":[],"tool_calls":["publish_vendor_catalog"]}""");

        await Assert.ThrowsAsync<MenuExtractionFailedException>(
            () => CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Extract_ToolShapedFieldNestedInsideAnItem_Throws()
    {
        // JsonUnmappedMemberHandling is NOT inherited. Without the attribute on WireCatalogItem
        // this payload would deserialize cleanly with `tool_calls` silently dropped — the
        // attacker-controlled half of the envelope being the permissive one, which is precisely
        // backwards.
        var handler = FakeHttpMessageHandler.RespondingJson(
            """
            {"items":[{"item_id":"i","name":"n","description":"d",
              "price":{"amount_minor":1,"currency":"AUD"},
              "tool_calls":["publish_vendor_catalog"]}],"flags":[]}
            """);

        await Assert.ThrowsAsync<MenuExtractionFailedException>(
            () => CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken));
    }

    [Theory]
    // Every nested record in the graph, one payload each: the attribute has to be on all of them.
    [InlineData("""{"items":[{"item_id":"i","name":"n","description":"d","price":{"amount_minor":1,"currency":"AUD","exec":"x"}}],"flags":[]}""")]
    [InlineData("""{"items":[{"item_id":"i","name":"n","description":"d","price":{"amount_minor":1,"currency":"AUD"},"availability_window":{"days_of_week":["mon"],"invoke":"x"}}],"flags":[]}""")]
    [InlineData("""{"items":[{"item_id":"i","name":"n","description":"d","price":{"amount_minor":1,"currency":"AUD"},"modifiers":[{"modifier_id":"m","name":"n","run":"x"}]}],"flags":[]}""")]
    [InlineData("""{"items":[{"item_id":"i","name":"n","description":"d","price":{"amount_minor":1,"currency":"AUD"},"modifiers":[{"modifier_id":"m","name":"n","price_delta":{"amount_minor":1,"currency":"AUD","call":"x"}}]}],"flags":[]}""")]
    [InlineData("""{"items":[],"flags":[{"item_id":null,"reason":"low_confidence","action":"publish"}]}""")]
    public async Task Extract_UnexpectedFieldInAnyNestedRecord_Throws(string json)
    {
        await Assert.ThrowsAsync<MenuExtractionFailedException>(
            () => CreateClient(FakeHttpMessageHandler.RespondingJson(json)).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Extract_ItemMissingARequiredField_Throws_RatherThanInventingADefault()
    {
        // A $0.00 item with no name is not a draft an owner can review; it is a fabricated row.
        var handler = FakeHttpMessageHandler.RespondingJson(
            """{"items":[{"item_id":"i","description":"d","price":{"amount_minor":1,"currency":"AUD"}}],"flags":[]}""");

        await Assert.ThrowsAsync<MenuExtractionFailedException>(
            () => CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2200)]
    [InlineData(long.MinValue)]
    public async Task Extract_NegativeAmountMinor_Throws_WithoutEchoingTheValue(long amountMinor)
    {
        // The Python side already refuses one (`_to_money` drops the row when `amount_minor < 0`),
        // but this class must not depend on the responder being that route — a drifted route, a dev
        // server or a proxy answers whatever it likes, which is the threat model the class documents.
        // Unchecked, the value lands in vendor_catalog_items.price_minor as a negative price on a
        // draft the owner is about to approve (Inv-16).
        var handler = FakeHttpMessageHandler.RespondingJson(
            $$$"""{"items":[{"item_id":"i","name":"n","description":"d","price":{"amount_minor":{{{amountMinor}}},"currency":"AUD"}}],"flags":[]}""");

        var ex = await Assert.ThrowsAsync<MenuExtractionFailedException>(
            () => CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken));

        // Response-derived, so it never reaches the message — same rule as every other body-shaped
        // value in this class (Inv-12/16). Not chained either: an inner exception is a leak with
        // extra steps.
        Assert.DoesNotContain(amountMinor.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task Extract_ZeroAmountMinor_IsAccepted()
    {
        // A free menu row is a real menu row, and the Python guard draws its line in exactly the
        // same place (`amount_minor < 0`). Rejecting zero would refuse a legitimate document.
        var handler = FakeHttpMessageHandler.RespondingJson(
            """{"items":[{"item_id":"i","name":"Complimentary bread","description":"On the house.","price":{"amount_minor":0,"currency":"AUD"}}],"flags":[]}""");

        var result = await CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken);

        Assert.Equal(0, Assert.Single(result.Items).PriceMinor);
    }

    [Theory]
    [InlineData("""{"items":null,"flags":null}""")]
    [InlineData("""{"items":null,"flags":[]}""")]
    [InlineData("""{"items":[],"flags":null}""")]
    [InlineData("""{"flags":[]}""")]   // items absent entirely
    [InlineData("""{"items":[]}""")]   // flags absent entirely
    [InlineData("{}")]                 // both absent
    public async Task Extract_NullOrAbsentItemsOrFlags_Throws_RatherThanCountingAsAnEmptyExtraction(string json)
    {
        // JsonUnmappedMemberHandling.Disallow says nothing about a member that is present-and-null
        // or missing, so every one of these deserialized happily and mapped to
        // MenuExtractionResult(0, 0) — ACCEPTED AS SUCCESS, settling the catalog to DraftReady with
        // zero items, which reads to the owner as "your menu is empty". Fail-open in a class whose
        // contract is "fail closed, always".
        //
        // The real route cannot emit this shape (`items` is required, `flags` has a default_factory,
        // and the envelope round-trips through model_validate(...).model_dump(mode="json")), so only
        // a non-conforming responder does — a reverse proxy, a drifted route, a dev server. Which is
        // precisely the threat model this class documents.
        await Assert.ThrowsAsync<MenuExtractionFailedException>(
            () => CreateClient(FakeHttpMessageHandler.RespondingJson(json))
                .ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Extract_MalformedJson_Throws_WithoutEchoingTheBody()
    {
        var handler = FakeHttpMessageHandler.RespondingJson($$"""{"items": [ {{Canary}} """);

        var ex = await Assert.ThrowsAsync<MenuExtractionFailedException>(
            () => CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken));

        AssertNothingLeaked(ex);
    }

    [Fact]
    public async Task Extract_NonJsonSuccessBody_Throws()
    {
        var handler = FakeHttpMessageHandler.Responding(HttpStatusCode.OK, Canary, "text/plain");

        var ex = await Assert.ThrowsAsync<MenuExtractionFailedException>(
            () => CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken));

        AssertNothingLeaked(ex);
    }

    // ---------------------------------------------------------------------
    // Transport failures
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]           // extraction_failed
    [InlineData(HttpStatusCode.ServiceUnavailable)]   // model_not_configured
    [InlineData(HttpStatusCode.Unauthorized)]         // bad or missing X-Cordia-Internal-Key
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    [InlineData(HttpStatusCode.UnprocessableContent)]
    public async Task Extract_NonSuccessStatus_ThrowsCarryingOnlyTheStatusCode(HttpStatusCode status)
    {
        var handler = FakeHttpMessageHandler.Responding(status, $$"""{"error":"{{Canary}}"}""");

        var ex = await Assert.ThrowsAsync<MenuExtractionFailedException>(
            () => CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken));

        Assert.Contains(((int)status).ToString(), ex.Message, StringComparison.Ordinal);
        AssertNothingLeaked(ex);
    }

    [Fact]
    public async Task Extract_TransportFailure_Throws()
    {
        var handler = new FakeHttpMessageHandler(
            (_, _) => throw new HttpRequestException("Connection refused (localhost:2024)"));

        await Assert.ThrowsAsync<MenuExtractionFailedException>(
            () => CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Extract_UnresolvableRef_PropagatesWithoutDialingOut()
    {
        var handler = FakeHttpMessageHandler.RespondingJson(MinimalOkResponse);

        // Not re-wrapped as MenuExtractionFailedException: the agent was never involved. The
        // ingestion handler settles both to ExtractFailed, but blaming the right component matters
        // to whoever reads the log.
        await Assert.ThrowsAsync<MenuArtifactUnresolvableException>(
            () => CreateClient(handler).ExtractAsync("s3://tenant/menu.pdf", TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.CallCount);
    }

    // ---------------------------------------------------------------------
    // D-F — the OperationCanceledException trap
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Extract_HttpClientTimeout_BecomesMenuExtractionFailed_NotOperationCanceled()
    {
        // THE load-bearing test. HttpClient signals its own timeout with a TaskCanceledException —
        // an OperationCanceledException — even though the caller cancelled nothing. Untranslated,
        // it would sail through IngestVendorMenuCommandHandler's
        // `catch (Exception ex) when (ex is not OperationCanceledException)`, roll back the inbox
        // transaction, un-admit the message and have it redelivered forever, re-extracting the same
        // document on every loop (Inv-5). This models the exception HttpClient actually throws.
        var handler = new FakeHttpMessageHandler((_, _) => throw new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 120 seconds elapsing.",
            new TimeoutException()));

        var ex = await Record.ExceptionAsync(
            () => CreateClient(handler).ExtractAsync(ArtifactRef, CancellationToken.None));

        Assert.IsType<MenuExtractionFailedException>(ex);
        Assert.False(ex is OperationCanceledException);
    }

    [Fact]
    public async Task Extract_OwnDeadlineElapses_BecomesMenuExtractionFailed_NotOperationCanceled()
    {
        // The same property, but through the REAL linked CTS rather than a synthesized exception:
        // the agent hangs, ExtractTimeoutSeconds elapses, and what comes out is still a
        // MenuExtractionFailedException. This is what proves the CancelAfter wiring exists at all.
        var handler = FakeHttpMessageHandler.Hanging();

        var ex = await Record.ExceptionAsync(
            () => CreateClient(handler, timeoutSeconds: 1).ExtractAsync(ArtifactRef, CancellationToken.None));

        Assert.IsType<MenuExtractionFailedException>(ex);
        Assert.False(ex is OperationCanceledException);
    }

    [Fact]
    public async Task Extract_CallerCancellation_StillPropagatesAsOperationCanceled()
    {
        // The other half of D-F: the `when (!ct.IsCancellationRequested)` filter must NOT swallow a
        // genuine cancel. A host shutdown mid-extraction has to abort the message, not settle the
        // catalog to ExtractFailed on the way out.
        var handler = FakeHttpMessageHandler.Hanging();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var ex = await Record.ExceptionAsync(
            () => CreateClient(handler, timeoutSeconds: 120).ExtractAsync(ArtifactRef, cts.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.IsNotType<MenuExtractionFailedException>(ex);
    }

    // ---------------------------------------------------------------------
    // The shared secret (D-G)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Extract_SecretConfigured_SendsTheInternalKeyHeader()
    {
        var handler = FakeHttpMessageHandler.RespondingJson(MinimalOkResponse);

        await CreateClient(handler, secret: "s3cr3t-ascii-only").ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken);

        Assert.Equal("s3cr3t-ascii-only", handler.LastHeaders[LangGraphMenuExtractorClient.InternalKeyHeader]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Extract_NoSecretConfigured_OmitsTheHeader(string? secret)
    {
        // Fail open in dev by design: `langgraph dev` binds an unauthenticated :2024 and the route
        // skips the check when CORDIA_MENU_EXTRACTION_API_KEY is unset. An empty header would be a
        // 401 against a route that IS configured, so absence must mean absence.
        var handler = FakeHttpMessageHandler.RespondingJson(MinimalOkResponse);

        await CreateClient(handler, secret: secret).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(LangGraphMenuExtractorClient.InternalKeyHeader, handler.LastHeaders.Keys);
    }

    // ---------------------------------------------------------------------
    // Control characters (carry-forward from Phase 3)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Extract_StripsControlCharactersFromEveryMappedField()
    {
        // These values land in `vendor_catalog_items`: text columns for the scalars, jsonb for
        // `modifiers` / `availability_window`. PostgreSQL rejects U+0000 in BOTH, and the throw
        // would happen at SaveChanges — outside the ingestion handler's fail-secure catch — so the
        // inbox transaction would roll back and the message would poison-loop (Inv-5). Same
        // reasoning as Phase 3's SanitizeAuditLabel on the audit path.
        var handler = FakeHttpMessageHandler.RespondingJson(
            """
            {
              "items": [
                {
                  "item_id": "item\u0000_1",
                  "name": "Grilled\u0000 Salmon",
                  "description": "Served\u0007 with\u001f greens.",
                  "price": {"amount_minor": 3200, "currency": "A\u0000UD"},
                  "category": "Ma\u0000ins",
                  "modifiers": [{"modifier_id": "mo\u0000d_1", "name": "Extra\u0000 lemon", "price_delta": {"amount_minor": 150, "currency": "AU\u0000D"}}],
                  "availability_window": {"days_of_week": ["mo\u0000n"], "start_time": "17\u0000:00", "end_time": "21:00"},
                  "image_ref": "s3://i\u0000mg/1.png",
                  "source_location": "page\u0000 2"
                }
              ],
              "flags": [{"item_id": "item\u0000_1", "reason": "suspicious\u0000_content"}]
            }
            """);

        var result = await CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal("item_1", item.ItemId);
        Assert.Equal("Grilled Salmon", item.Name);
        Assert.Equal("Served with greens.", item.Description);
        Assert.Equal("AUD", item.Currency);
        Assert.Equal("Mains", item.Category);
        Assert.Equal("s3://img/1.png", item.ImageRef);
        Assert.Equal("page 2", item.SourceLocation);

        // The escaped form matters as much as the raw one: JsonSerializer would re-emit an embedded
        // NUL as the six ASCII characters \u0000, which a control-character filter run over the
        // SERIALIZED text could not see — and which jsonb rejects just the same. Stripping has to
        // happen BEFORE serialization, and this is what says so.
        Assert.DoesNotContain("\\u0000", item.Modifiers, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0000", item.AvailabilityWindow, StringComparison.Ordinal);
        Assert.Contains("mod_1", item.Modifiers, StringComparison.Ordinal);
        Assert.Contains("Extra lemon", item.Modifiers, StringComparison.Ordinal);
        Assert.Contains("\"AUD\"", item.Modifiers, StringComparison.Ordinal);
        Assert.Contains("\"mon\"", item.AvailabilityWindow, StringComparison.Ordinal);
        Assert.Contains("17:00", item.AvailabilityWindow, StringComparison.Ordinal);

        var flag = Assert.Single(result.Flags);
        Assert.Equal("item_1", flag.ItemId);
        Assert.Equal("suspicious_content", flag.Reason);

        AssertNoControlCharacters(
            item.ItemId, item.Name, item.Description, item.Currency, item.Category!,
            item.ImageRef!, item.SourceLocation!, item.Modifiers!, item.AvailabilityWindow!,
            flag.ItemId!, flag.Reason);
    }

    [Fact]
    public async Task Extract_BoundsItemIdLength_BecauseItIsHalfABtreeKey()
    {
        // THE poison-loop guard. `vendor_catalog_items` PK is btree (tenant_id, item_id) and
        // PostgreSQL refuses an index tuple over 2704 bytes:
        //     ERROR: index row size 6416 exceeds btree version 4 maximum 2704
        // It throws at SaveChanges — after the ingestion handler has returned, outside its
        // fail-secure catch — so the inbox rolls back and a paid LLM extraction re-runs forever
        // (Inv-5). HIGH-ENTROPY on purpose: btree compresses index tuples, so a repetitive
        // `repeat('x', 3000)` id inserts happily and only realistic hostile content (a base64
        // blob or a hash printed on a menu) actually trips it. A low-entropy test would pass
        // against a broken implementation.
        var hostileId = string.Concat(
            Enumerable.Range(0, 10_000).Select(i => "0123456789abcdef"[(i * 7919) % 16]));
        Assert.Equal(10_000, hostileId.Length);

        var handler = FakeHttpMessageHandler.RespondingJson(
            $$$"""
            {"items":[{"item_id":"{{{hostileId}}}","name":"n","description":"d",
              "price":{"amount_minor":1,"currency":"AUD"}}],
             "flags":[{"item_id":"{{{hostileId}}}","reason":"low_confidence"}]}
            """);

        var result = await CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal(LangGraphMenuExtractorClient.MaxItemIdChars, item.ItemId.Length);
        Assert.StartsWith(item.ItemId, hostileId, StringComparison.Ordinal);

        // Even at the worst-case 4 UTF-8 bytes per char this leaves room for the handler's
        // "{CatalogId}:" prefix and the tenant id inside the 2704-byte index-tuple budget.
        Assert.True(item.ItemId.Length * 4 < 2704);

        var flag = Assert.Single(result.Flags);
        Assert.Equal(LangGraphMenuExtractorClient.MaxItemIdChars, flag.ItemId!.Length);
    }

    // ---------------------------------------------------------------------
    // Sanitize is not injective: colliding item ids must fail HERE
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Extract_TwoItemIdsDifferingOnlyPastTheTruncationBound_Throws()
    {
        // Truncation to MaxItemIdChars collapses them. Python dedupes `item_id` at FULL length on the
        // raw string (_unique_item_id) and ExtractedMenuItem.item_id has no max_length, so nothing
        // upstream can prevent it — and both ids are attacker-supplied document content.
        //
        // Untranslated, the duplicate throws at AddRange on EF's identity map ("another instance with
        // the same key value for {'TenantId', 'ItemId'} is already being tracked"), or as a
        // duplicate-key violation at SaveChanges on a redelivery. Both land AFTER
        // IngestVendorMenuCommandHandler's fail-secure catch has closed, so the inbox transaction
        // rolls back and a paid LLM extraction re-runs on every redelivery (Inv-5).
        var shared = new string('a', LangGraphMenuExtractorClient.MaxItemIdChars);
        var handler = FakeHttpMessageHandler.RespondingJson(
            $$$"""
            {"items":[
              {"item_id":"{{{shared}}}one","name":"Soup","description":"d","price":{"amount_minor":1,"currency":"AUD"}},
              {"item_id":"{{{shared}}}two","name":"Salad","description":"d","price":{"amount_minor":2,"currency":"AUD"}}
            ],"flags":[]}
            """);

        await Assert.ThrowsAsync<MenuExtractionFailedException>(
            () => CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Extract_TwoItemIdsDifferingOnlyByAnEmbeddedControlCharacter_Throws()
    {
        // The cheaper vector, and the one no length bound would catch: "SKU<TAB>A1" and "SKUA1" are
        // distinct strings in Python and identical after the control-character strip.
        var handler = FakeHttpMessageHandler.RespondingJson(
            """
            {"items":[
              {"item_id":"SKU\tA1","name":"Soup","description":"d","price":{"amount_minor":1,"currency":"AUD"}},
              {"item_id":"SKUA1","name":"Salad","description":"d","price":{"amount_minor":2,"currency":"AUD"}}
            ],"flags":[]}
            """);

        await Assert.ThrowsAsync<MenuExtractionFailedException>(
            () => CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Extract_CollidingItemIds_AreNotLeakedIntoTheException()
    {
        // The ids are model-shaped text read out of an untrusted document (Inv-12/16), so the
        // failure names the fault, never the value.
        var handler = FakeHttpMessageHandler.RespondingJson(
            $$$"""
            {"items":[
              {"item_id":"{{{Canary}}}","name":"Soup","description":"d","price":{"amount_minor":1,"currency":"AUD"}},
              {"item_id":"{{{Canary}}}","name":"Salad","description":"d","price":{"amount_minor":2,"currency":"AUD"}}
            ],"flags":[]}
            """);

        var ex = await Assert.ThrowsAsync<MenuExtractionFailedException>(
            () => CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken));

        AssertNothingLeaked(ex);
    }

    [Fact]
    public async Task Extract_DistinctItemIdsThatShareALongPrefix_AreStillAccepted()
    {
        // The check must be a COLLISION check, not a similarity check: two ids that survive
        // sanitization distinct are a perfectly ordinary two-item menu, and refusing them would
        // settle a readable document to ExtractFailed for no reason.
        var shared = new string('a', LangGraphMenuExtractorClient.MaxItemIdChars - 1);
        var handler = FakeHttpMessageHandler.RespondingJson(
            $$$"""
            {"items":[
              {"item_id":"{{{shared}}}1","name":"Soup","description":"d","price":{"amount_minor":1,"currency":"AUD"}},
              {"item_id":"{{{shared}}}2","name":"Salad","description":"d","price":{"amount_minor":2,"currency":"AUD"}}
            ],"flags":[]}
            """);

        var result = await CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2, result.Items.Select(i => i.ItemId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Extract_DuplicateFlagItemIds_AreStillAccepted()
    {
        // Only ITEM ids are a database key. Two flags may legitimately name the same item — a
        // low-confidence price and a suspicious description on one row — and nothing about the flags
        // list is unique-constrained.
        var handler = FakeHttpMessageHandler.RespondingJson(
            """
            {"items":[{"item_id":"i","name":"n","description":"d","price":{"amount_minor":1,"currency":"AUD"}}],
             "flags":[{"item_id":"i","reason":"low_confidence"},{"item_id":"i","reason":"suspicious_content"}]}
            """);

        var result = await CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Flags.Count);
    }

    [Theory]
    [InlineData("name", 512)]
    [InlineData("description", 4000)]
    [InlineData("category", 128)]
    [InlineData("source_location", 512)]
    [InlineData("image_ref", 1024)]
    public async Task Extract_BoundsEveryMappedStringField(string field, int expected)
    {
        // None of these columns declares a HasMaxLength, so this mapping boundary is the only
        // bound that exists between an attacker-supplied document and an unbounded text column.
        var oversize = new string('x', 20_000);
        var handler = FakeHttpMessageHandler.RespondingJson(
            $$$"""
            {"items":[{"item_id":"i","name":"n","description":"d",
              "price":{"amount_minor":1,"currency":"AUD"},"{{{field}}}":"{{{oversize}}}"}],"flags":[]}
            """);

        var result = await CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        var actual = field switch
        {
            "name" => item.Name,
            "description" => item.Description,
            "category" => item.Category,
            "source_location" => item.SourceLocation,
            _ => item.ImageRef,
        };
        Assert.Equal(expected, actual!.Length);
    }

    [Fact]
    public async Task Extract_BoundsCurrencyAndModifierCount_KeepingTheJsonValid()
    {
        var manyModifiers = string.Join(",", Enumerable.Range(0, 500).Select(i =>
            $$$"""{"modifier_id":"m{{{i}}}","name":"{{{new string('y', 2000)}}}"}"""));
        var handler = FakeHttpMessageHandler.RespondingJson(
            $$$"""
            {"items":[{"item_id":"i","name":"n","description":"d",
              "price":{"amount_minor":1,"currency":"{{{new string('Z', 900)}}}"},
              "modifiers":[{{{manyModifiers}}}]}],"flags":[]}
            """);

        var result = await CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal(LangGraphMenuExtractorClient.MaxCurrencyChars, item.Currency.Length);

        // Bounded, and — critically — still parseable. A jsonb column parses on insert, so a
        // TRUNCATED JSON document would throw at SaveChanges: the very poison loop being guarded.
        Assert.NotNull(item.Modifiers);
        using var parsed = JsonDocument.Parse(item.Modifiers!);
        Assert.Equal(LangGraphMenuExtractorClient.MaxModifiers, parsed.RootElement.GetArrayLength());
        Assert.True(item.Modifiers!.Length <= LangGraphMenuExtractorClient.MaxJsonColumnChars);
    }

    [Fact]
    public async Task Extract_TruncationNeverSplitsASurrogatePair()
    {
        // Cutting mid-pair leaves a lone high surrogate, which is not valid text and which the
        // JSON writer would have to replace with U+FFFD on the jsonb path.
        var handler = FakeHttpMessageHandler.RespondingJson(
            $$$"""
            {"items":[{"item_id":"i","name":"{{{string.Concat(Enumerable.Repeat("😀", 400))}}}",
              "description":"d","price":{"amount_minor":1,"currency":"AUD"}}],"flags":[]}
            """);

        var result = await CreateClient(handler).ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken);

        var name = Assert.Single(result.Items).Name;
        Assert.True(name.Length <= LangGraphMenuExtractorClient.MaxNameChars);
        Assert.False(char.IsHighSurrogate(name[^1]), "truncation split a surrogate pair");
    }

    // ---------------------------------------------------------------------
    // Request shaping: artifact_ref (finding 2) and correlation (finding 3)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Extract_LongArtifactRef_IsOmittedFromTheBody()
    {
        // `inline:<the whole menu>` is the canonical ref, so echoing it would put the document in
        // the body TWICE. The route's MAX_REQUEST_BODY_BYTES is 4× the text budget — a transport
        // guard sized to absorb JSON escaping of ONE copy, not to carry a second one — so an at-cap
        // document plus its own ref spends over half of it on a field the route only reduces to a
        // digest anyway. The ref is also a value this codebase refuses to log raw (Inv-12).
        var bigMenu = new string('m', 5000);
        var handler = FakeHttpMessageHandler.RespondingJson(MinimalOkResponse);

        await CreateClient(handler).ExtractAsync("inline:" + bigMenu, TestContext.Current.CancellationToken);

        using var sent = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(bigMenu, sent.RootElement.GetProperty("menu_text").GetString());
        Assert.Equal(JsonValueKind.Null, sent.RootElement.GetProperty("artifact_ref").ValueKind);

        // The document appears once, not twice.
        Assert.Equal(1, CountOccurrences(handler.LastBody!, bigMenu));
    }

    [Fact]
    public async Task Extract_ShortArtifactRef_IsStillSentForCorrelation()
    {
        var handler = FakeHttpMessageHandler.RespondingJson(MinimalOkResponse);

        const string shortRef = "data:text/plain,Soup%20of%20the%20Day";
        await CreateClient(handler).ExtractAsync(shortRef, TestContext.Current.CancellationToken);

        using var sent = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(shortRef, sent.RootElement.GetProperty("artifact_ref").GetString());
        Assert.Equal("Soup of the Day", sent.RootElement.GetProperty("menu_text").GetString());
    }

    [Fact]
    public async Task Extract_SendsTheCorrelationHeader_WhenOneIsAmbient()
    {
        // With artifact_ref usually omitted this is the only way to join a .NET "HTTP 502" line to
        // the route's own log line. Starlette ignores unknown headers, so it costs nothing.
        var handler = FakeHttpMessageHandler.RespondingJson(MinimalOkResponse);

        await CreateClient(handler, correlationId: "corr_abc123")
            .ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken);

        Assert.Equal("corr_abc123", handler.LastHeaders[LangGraphMenuExtractorClient.CorrelationHeader]);
    }

    [Fact]
    public async Task Extract_OmitsTheCorrelationHeader_WhenNoneIsAmbient()
    {
        var handler = FakeHttpMessageHandler.RespondingJson(MinimalOkResponse);

        await CreateClient(handler, correlationId: "").ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(LangGraphMenuExtractorClient.CorrelationHeader, handler.LastHeaders.Keys);
    }

    [Fact]
    public async Task Extract_TrimsTheSecret_BecauseTheRouteStripsItsOwn()
    {
        // The route compares against `os.environ.get(...).strip()`. An untrimmed "  k  " would
        // fail the constant-time compare against "k" and 401 forever, with nothing on either side
        // of the wire to explain why.
        var handler = FakeHttpMessageHandler.RespondingJson(MinimalOkResponse);

        await CreateClient(handler, secret: "  s3cr3t  ").ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken);

        Assert.Equal("s3cr3t", handler.LastHeaders[LangGraphMenuExtractorClient.InternalKeyHeader]);
    }

    [Fact]
    public async Task Extract_WhitespaceOnlySecret_IsTreatedAsAbsent()
    {
        // It strips to nothing on the route's side, so sending it would be a guaranteed 401.
        var handler = FakeHttpMessageHandler.RespondingJson(MinimalOkResponse);

        await CreateClient(handler, secret: "   ").ExtractAsync(ArtifactRef, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(LangGraphMenuExtractorClient.InternalKeyHeader, handler.LastHeaders.Keys);
    }

    // ---------------------------------------------------------------------
    // D-F, second half: the resolver shares the deadline (finding 4)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Extract_ResolverTimesOut_BecomesMenuExtractionFailed_NotOperationCanceled()
    {
        // InlineMenuArtifactTextResolver is pure and synchronous, so this cannot happen today —
        // but the port exists so #84 can put object storage behind it, doing network I/O with its
        // own timeout. On that day an S3 timeout raises TaskCanceledException with the caller's
        // token uncancelled; if the resolver call sat outside the try it would sail past the D-F
        // translation, past the handler's fail-secure catch, and poison-loop the message (Inv-5).
        var handler = FakeHttpMessageHandler.RespondingJson(MinimalOkResponse);
        var resolver = new TimingOutResolver();

        var ex = await Record.ExceptionAsync(() =>
            CreateClient(handler, resolver: resolver).ExtractAsync(ArtifactRef, CancellationToken.None));

        Assert.IsType<MenuExtractionFailedException>(ex);
        Assert.False(ex is OperationCanceledException);
        Assert.Equal(0, handler.CallCount);
    }

    private sealed class TimingOutResolver : IMenuArtifactTextResolver
    {
        /// <summary>Models an object-store client whose own HttpClient.Timeout elapsed.</summary>
        public Task<string> ResolveTextAsync(string artifactRef, CancellationToken ct = default)
            => throw new TaskCanceledException("simulated object-store timeout", new TimeoutException());
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private readonly MenuExtractorHarness _harness = new();

    public void Dispose()
    {
        _harness.Dispose();
        GC.SuppressFinalize(this);
    }

    private LangGraphMenuExtractorClient CreateClient(
        FakeHttpMessageHandler handler,
        string? secret = null,
        int timeoutSeconds = 30,
        IMenuArtifactTextResolver? resolver = null,
        string correlationId = "")
        => _harness.Create(handler, secret, timeoutSeconds, resolver, correlationId);

    private static void AssertNothingLeaked(Exception ex)
    {
        Assert.DoesNotContain(Canary, ex.Message, StringComparison.Ordinal);

        // ToString() as well as Message: it renders the whole chain plus stack, so a future
        // `throw new …(msg, inner)` regression would leak through it while Message stayed clean.
        Assert.DoesNotContain(Canary, ex.ToString(), StringComparison.Ordinal);

        // Not chained either: a JsonException names the offending property and a provider error
        // quotes the request back, so an inner exception is a leak with extra steps.
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            Assert.DoesNotContain(Canary, inner.Message, StringComparison.Ordinal);
    }

    private static void AssertNoControlCharacters(params string[] values)
        => Assert.All(values, v => Assert.False(v.Any(char.IsControl), $"control character survived in \"{v}\""));
}
