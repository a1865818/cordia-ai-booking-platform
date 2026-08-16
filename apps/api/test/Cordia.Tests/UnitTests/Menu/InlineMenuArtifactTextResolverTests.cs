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
        // JSON body. The route allows MAX_REQUEST_BODY_BYTES = 2 × MAX_MENU_TEXT_BYTES, sized to
        // absorb JSON ESCAPING — every quote, newline and control character in a menu costs extra
        // bytes once escaped. The client omits a long artifact_ref precisely so the document is
        // not sent twice; this pins the remaining headroom so a future change that starts echoing
        // the ref (or adds a field) goes red here rather than as a 413 in production.
        const int routeBodyBudget = 2 * InlineMenuArtifactTextResolver.MaxMenuTextBytes;

        // Worst realistic case: a menu where every character needs a 2-byte JSON escape would not
        // fit, but a normal one (~3% quotes/newlines) leaves ~249 KiB of usable text. Assert the
        // envelope overhead for an at-cap document with no artifact_ref.
        var atCap = new string('a', InlineMenuArtifactTextResolver.MaxMenuTextBytes);
        var body = JsonSerializer.Serialize(new { menu_text = atCap, artifact_ref = (string?)null });

        Assert.True(
            Encoding.UTF8.GetByteCount(body) < routeBodyBudget,
            "an at-cap document must round-trip inside the route's body budget once artifact_ref is omitted");

        // And the thing that used to be false: echoing the ref back doubles it and blows the budget.
        var withRef = JsonSerializer.Serialize(new { menu_text = atCap, artifact_ref = "inline:" + atCap });
        Assert.True(Encoding.UTF8.GetByteCount(withRef) > routeBodyBudget);
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
