using System.Text;
using System.Text.Json;
using Cordia.Domain.Exceptions;
using Cordia.Infrastructure.Menu;

namespace Cordia.Tests.UnitTests.Menu;

/// <summary>
/// Unit tests for <see cref="InlineMenuArtifactTextResolver"/> — the D-B minimal slice of
/// artifact-ref → menu text (issue #87).
/// </summary>
[Trait("Category", "Unit")]
public class InlineMenuArtifactTextResolverTests
{
    private readonly InlineMenuArtifactTextResolver _resolver = new();

    [Fact]
    public async Task Resolve_InlineRef_ReturnsTheLiteralText()
    {
        var text = await _resolver.ResolveTextAsync("inline:Grilled Salmon - $32.00", TestContext.Current.CancellationToken);

        Assert.Equal("Grilled Salmon - $32.00", text);
    }

    [Fact]
    public async Task Resolve_Base64DataRef_DecodesUtf8()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("Café Menu — Crème Brûlée"));

        var text = await _resolver.ResolveTextAsync($"data:text/plain;base64,{encoded}", TestContext.Current.CancellationToken);

        Assert.Equal("Café Menu — Crème Brûlée", text);
    }

    [Fact]
    public async Task Resolve_PercentEncodedDataRef_Unescapes()
    {
        var text = await _resolver.ResolveTextAsync("data:text/plain,Soup%20of%20the%20Day%20%2D%20%248.00", TestContext.Current.CancellationToken);

        Assert.Equal("Soup of the Day - $8.00", text);
    }

    [Fact]
    public async Task Resolve_Base64PrefixWins_OverThePlainDataPrefix()
    {
        // `;base64` is what selects base64 decoding; a parser that missed it would hand the blob
        // back as literal menu text — a successful-looking extraction of gibberish rather than an
        // honest failure.
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("Real menu"));

        Assert.Equal("Real menu", await _resolver.ResolveTextAsync($"data:text/plain;base64,{encoded}", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("s3://tenant-42/menus/spring.pdf")]  // #84 territory: deliberately unsupported
    [InlineData("blob://container/menu.txt")]
    [InlineData("https://example.com/menu.txt")]     // no fetching: SSRF surface we do not open
    [InlineData("data:application/pdf;base64,JVBERi0=")]
    [InlineData("file:///etc/passwd")]
    [InlineData("just some text with no scheme at all")]
    [InlineData("")]
    public async Task Resolve_UnsupportedRef_Throws_WithoutNamingTheRef(string artifactRef)
    {
        var ex = await Assert.ThrowsAsync<MenuArtifactUnresolvableException>(
            () => _resolver.ResolveTextAsync(artifactRef, TestContext.Current.CancellationToken));

        AssertRefIsNotLeaked(ex, artifactRef);
    }

    [Fact]
    public async Task Resolve_OverCapMultibyteText_Throws_BecauseTheCapIsBytesNotChars()
    {
        // 100_000 '€' is 100_000 CHARS (comfortably under the 262_144 cap read as string.Length)
        // but 300_000 UTF-8 BYTES (comfortably over it). A `string.Length` check would sail past
        // this and the oversize body would come back as a 413 from the Python route only after the
        // request had been built and sent — the two boundaries must agree on what "too big" means.
        var oversize = new string('€', 100_000);
        Assert.True(oversize.Length < InlineMenuArtifactTextResolver.MaxMenuTextBytes);
        Assert.True(Encoding.UTF8.GetByteCount(oversize) > InlineMenuArtifactTextResolver.MaxMenuTextBytes);

        var ex = await Assert.ThrowsAsync<MenuArtifactUnresolvableException>(
            () => _resolver.ResolveTextAsync("inline:" + oversize, TestContext.Current.CancellationToken));

        Assert.Contains("256 KiB", ex.Message);
        Assert.DoesNotContain("€", ex.Message);
    }

    [Fact]
    public async Task Resolve_TextExactlyAtTheCap_IsAccepted()
    {
        var atCap = new string('a', InlineMenuArtifactTextResolver.MaxMenuTextBytes);

        var text = await _resolver.ResolveTextAsync("inline:" + atCap, TestContext.Current.CancellationToken);

        Assert.Equal(InlineMenuArtifactTextResolver.MaxMenuTextBytes, Encoding.UTF8.GetByteCount(text));
    }

    [Fact]
    public void ResolverCap_FitsInsideTheRoutesRequestBodyBudget()
    {
        // The resolver's cap is not the whole story: the text still has to survive the trip as a
        // JSON body. The route allows MAX_REQUEST_BODY_BYTES = 4 × MAX_MENU_TEXT_BYTES, a transport
        // guard sized to absorb JSON ESCAPING of ONE copy of the document — every quote, newline and
        // control character in a menu costs extra bytes once escaped, and a pathological menu can
        // cost six per character. The client omits a long artifact_ref precisely so the document is
        // not sent twice; this pins the remaining headroom so a future change that starts echoing the
        // ref (or adds a field) goes red here rather than as a 413 in production.
        const int routeBodyBudget = 4 * InlineMenuArtifactTextResolver.MaxMenuTextBytes;

        var atCap = new string('a', InlineMenuArtifactTextResolver.MaxMenuTextBytes);
        var body = JsonSerializer.Serialize(new { menu_text = atCap, artifact_ref = (string?)null });

        Assert.True(
            Encoding.UTF8.GetByteCount(body) < routeBodyBudget,
            "an at-cap document must round-trip inside the route's body budget once artifact_ref is omitted");

        // And the reason the ref is omitted rather than merely tolerated: echoing it back spends a
        // SECOND full copy of the document out of a budget sized for the escaping of one, leaving an
        // at-cap upload with no room for it. Asserted against the copy, not the budget, so it stays
        // meaningful whichever multiple the route picks.
        var withRef = JsonSerializer.Serialize(new { menu_text = atCap, artifact_ref = "inline:" + atCap });
        Assert.True(
            Encoding.UTF8.GetByteCount(withRef) - Encoding.UTF8.GetByteCount(body)
                >= InlineMenuArtifactTextResolver.MaxMenuTextBytes,
            "echoing the ref must cost a whole extra copy of the document — that is why it is omitted");
    }

    [Theory]
    [InlineData("data:text/plain;charset=utf-8;base64,")]
    [InlineData("data:text/plain;charset=UTF-8;base64,")]
    [InlineData("data:text/plain;base64,")]
    public async Task Resolve_Base64WithRfc2397Parameters_IsAccepted(string prefix)
    {
        // `;charset=utf-8` is legal RFC 2397 and commonly emitted. Refusing it would have failed
        // an upload for a reason no owner could act on.
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("Grilled Salmon"));

        Assert.Equal("Grilled Salmon",
            await _resolver.ResolveTextAsync(prefix + encoded, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Resolve_PercentEncodedWithParameters_IsAccepted()
    {
        Assert.Equal("Soup, salad",
            await _resolver.ResolveTextAsync(
                "data:text/plain;charset=utf-8,Soup%2C%20salad", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Resolve_SplitsOnTheFirstComma_SoAPayloadMayContainCommas()
    {
        // A literal comma is ordinary in a menu line; splitting on the last one would truncate it.
        Assert.Equal("Soup, salad, bread",
            await _resolver.ResolveTextAsync(
                "data:text/plain,Soup, salad, bread", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Resolve_OverCapBase64_Throws()
    {
        var encoded = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(new string('a', InlineMenuArtifactTextResolver.MaxMenuTextBytes + 1)));

        var ex = await Assert.ThrowsAsync<MenuArtifactUnresolvableException>(
            () => _resolver.ResolveTextAsync($"data:text/plain;base64,{encoded}", TestContext.Current.CancellationToken));

        Assert.Contains("256 KiB", ex.Message);
    }

    [Fact]
    public async Task Resolve_MalformedBase64_Throws_WithoutNamingTheRef()
    {
        const string artifactRef = "data:text/plain;base64,!!!this-is-not-base64!!!";

        var ex = await Assert.ThrowsAsync<MenuArtifactUnresolvableException>(
            () => _resolver.ResolveTextAsync(artifactRef, TestContext.Current.CancellationToken));

        AssertRefIsNotLeaked(ex, artifactRef);
        // The FormatException is not chained either — nothing derived from the ref reaches a log.
        Assert.Null(ex.InnerException);
    }

    [Theory]
    [InlineData("inline:")]
    [InlineData("inline:   \t\n  ")]
    [InlineData("data:text/plain,")]
    public async Task Resolve_BlankText_Throws_RatherThanReturningAnEmptyString(string artifactRef)
    {
        // An empty menu is indistinguishable from a successful extraction of nothing: the handler
        // would settle the catalog to DraftReady with zero items, which reads to the owner as
        // "your menu is empty" instead of "we could not read your upload".
        await Assert.ThrowsAsync<MenuArtifactUnresolvableException>(
            () => _resolver.ResolveTextAsync(artifactRef, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Resolve_AlreadyCancelledToken_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _resolver.ResolveTextAsync("inline:menu", cts.Token));
    }

    // ---------------------------------------------------------------------
    // Invalid UTF-8 is a failed read, not a silently altered one
    // ---------------------------------------------------------------------

    /// <summary>
    /// A Latin-1/CP1252 menu base64-encoded without transcoding, and a truncated multi-byte
    /// sequence — the two realistic ways an upload arrives as non-UTF-8 bytes.
    ///
    /// <para>The static <see cref="Encoding.UTF8"/> carries the REPLACEMENT fallback, so
    /// <c>53-6F-75-70-20-FF-FE-20-24-39</c> came back as <c>"Soup �� $9"</c> with no exception. That
    /// mangled text went to the model: item names carrying replacement characters, prices possibly
    /// corrupted, and all of it landing in <c>vendor_catalog_items.name</c> for owner review with
    /// nothing recording that the bytes were unreadable — which defeats the one distinction this
    /// resolver exists to draw, between "we could not read your upload" and a successful read.</para>
    /// </summary>
    [Theory]
    // "Soup " + FF FE + " $9" — the finding's exact trigger, `data:text/plain;base64,U291cCD//iAkOQ==`.
    [InlineData(new byte[] { 0x53, 0x6F, 0x75, 0x70, 0x20, 0xFF, 0xFE, 0x20, 0x24, 0x39 })]
    [InlineData(new byte[] { 0x43, 0x61, 0x66, 0xE9 })]              // "Café" in Latin-1
    [InlineData(new byte[] { 0x43, 0x61, 0x66, 0xC3 })]              // truncated 2-byte sequence
    [InlineData(new byte[] { 0xE2, 0x82 })]                          // truncated 3-byte sequence
    [InlineData(new byte[] { 0xC0, 0xAF })]                          // overlong encoding of '/'
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })]                    // UTF-16 surrogate, illegal in UTF-8
    public async Task Resolve_Base64OfInvalidUtf8_Throws_RatherThanSubstitutingReplacementCharacters(byte[] bytes)
    {
        var artifactRef = $"data:text/plain;base64,{Convert.ToBase64String(bytes)}";

        var ex = await Assert.ThrowsAsync<MenuArtifactUnresolvableException>(
            () => _resolver.ResolveTextAsync(artifactRef, TestContext.Current.CancellationToken));

        Assert.Contains("UTF-8", ex.Message, StringComparison.Ordinal);
        AssertRefIsNotLeaked(ex, artifactRef);
        // Not chained: a DecoderFallbackException names the offending byte offset in content that IS
        // the uploaded document.
        Assert.Null(ex.InnerException);
    }

    /// <summary>
    /// The percent-encoded half of the same defect. <b>Measured, because the mechanism is not the
    /// obvious one:</b> <see cref="Uri.UnescapeDataString"/> does NOT substitute <c>U+FFFD</c> — it
    /// leaves an undecodable escape run as LITERAL text (<c>"Soup%20%FF%FE%20%249"</c> came back as
    /// <c>"Soup %FF%FE $9"</c>). Milder than mojibake, same defect: unreadable bytes reaching the
    /// model as filler instead of being reported as an upload we could not read.
    /// </summary>
    [Theory]
    [InlineData("data:text/plain,Soup%20%FF%FE%20%249")]
    [InlineData("data:text/plain,Caf%E9%20Menu")]        // Latin-1 percent-encoding
    [InlineData("data:text/plain,Caf%C3")]               // truncated 2-byte sequence
    [InlineData("data:text/plain;charset=utf-8,%E2%82")] // truncated 3-byte sequence
    public async Task Resolve_PercentEncodedInvalidUtf8_Throws(string artifactRef)
    {
        var ex = await Assert.ThrowsAsync<MenuArtifactUnresolvableException>(
            () => _resolver.ResolveTextAsync(artifactRef, TestContext.Current.CancellationToken));

        Assert.Contains("UTF-8", ex.Message, StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
    }

    [Theory]
    // Multi-byte characters are consecutive escapes and must be decoded as one run, not per byte.
    [InlineData("data:text/plain,Caf%C3%A9%20%E2%80%94%20Cr%C3%A8me", "Café — Crème")]
    // A malformed escape stays LITERAL, exactly as Uri.UnescapeDataString leaves it: refusing an
    // upload over a stray percent sign in a price would be a failure no owner could act on.
    [InlineData("data:text/plain,50%25 off", "50% off")]
    [InlineData("data:text/plain,50% off", "50% off")]
    [InlineData("data:text/plain,Soup 100%", "Soup 100%")]
    // "% 1" must NOT be read as the byte 0x01 — NumberStyles.HexNumber would have, because it also
    // allows leading/trailing whitespace.
    [InlineData("data:text/plain,discount% 1 today", "discount% 1 today")]
    [InlineData("data:text/plain,Soup%2zsalad", "Soup%2zsalad")]
    // Escaped and literal text interleaved, so the run-flushing order is pinned too.
    [InlineData("data:text/plain,a%C3%A9b%E2%80%94c", "aéb—c")]
    public async Task Resolve_PercentEncoding_MatchesTheDocumentedSemantics(string artifactRef, string expected)
    {
        Assert.Equal(expected, await _resolver.ResolveTextAsync(artifactRef, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Resolve_ValidUtf8_NeverReturnsAReplacementCharacter()
    {
        // The positive half: a correctly-encoded multibyte document round-trips byte-exact through
        // the strict decoder, so the throw above is about invalid bytes and not about multibyte text.
        const string menu = "Café Menu — Crème Brûlée 🍰 ¥1,200";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(menu));

        var text = await _resolver.ResolveTextAsync(
            $"data:text/plain;base64,{encoded}", TestContext.Current.CancellationToken);

        Assert.Equal(menu, text);
        Assert.DoesNotContain('�', text);
    }

    /// <summary>
    /// The reference is a <c>SensitiveDataMask</c> control-plane key whose inline form IS the
    /// whole uploaded document; an exception message carrying it would push untrusted content
    /// straight into the logs (Inv-12).
    /// </summary>
    private static void AssertRefIsNotLeaked(Exception ex, string artifactRef)
    {
        if (artifactRef.Length > 0)
            Assert.DoesNotContain(artifactRef, ex.Message, StringComparison.Ordinal);
    }
}
