using System.Text;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;

namespace Cordia.Infrastructure.Menu;

/// <summary>
/// Turns the inline artifact references the console can send today into menu text
/// (issue #87 D-B). Three forms and no more:
///
/// <list type="bullet">
///   <item><c>inline:&lt;literal menu text&gt;</c></item>
///   <item><c>data:text/plain[;param=value…][;base64],&lt;payload&gt;</c> (RFC 2397)</item>
/// </list>
///
/// <para>RFC-2397 parameters between the media type and the payload are tolerated and ignored —
/// <c>data:text/plain;charset=utf-8;base64,…</c> is a legal, commonly-emitted form, and rejecting
/// it would have failed an upload for a reason no owner could act on. Only <c>;base64</c> changes
/// behaviour; the payload is UTF-8 either way, because that is the only encoding the extraction
/// route accepts.</para>
///
/// <para><b>Object-storage references (<c>s3://</c>, <c>blob://</c>, …) arrive with #84, behind
/// this same port.</b> Nothing above this class moves when they do — that is the whole reason the
/// resolver is a separate seam from <see cref="IIsolatedMenuExtractor"/>. Until then such a
/// reference is a <see cref="MenuArtifactUnresolvableException"/>, so the ingestion settles to
/// <c>ExtractFailed</c> instead of extracting a plausible-looking empty menu. <b>Do not grow
/// object storage here.</b></para>
///
/// <para><b>The cap is measured in UTF-8 bytes, not characters.</b> The Python route enforces
/// <c>MAX_MENU_TEXT_BYTES = 256 KiB</c> against <c>len(menu_text.encode("utf-8"))</c>; a
/// <c>string.Length</c> check on this side would let a CJK or emoji document up to three or four
/// times the budget through, and it would come back as a <c>413</c> after the request had already
/// been built and sent. Both boundaries must agree on what "too big" means.</para>
///
/// <para>Stateless and thread-safe → registered as a singleton.</para>
/// </summary>
public sealed class InlineMenuArtifactTextResolver : IMenuArtifactTextResolver
{
    /// <summary>
    /// Ceiling on the resolved text, in UTF-8 bytes. Mirrors <c>MAX_MENU_TEXT_BYTES</c> in
    /// <c>apps/langgraph/cordia_agent/menu/http_app.py</c> — change both or neither.
    /// </summary>
    internal const int MaxMenuTextBytes = 256 * 1024;

    private const string InlinePrefix = "inline:";
    private const string DataPrefix = "data:text/plain";
    private const string Base64Parameter = "base64";

    // NONE of these messages may name the reference. `source_artifact_ref` is a
    // SensitiveDataMask control-plane key (copied verbatim wherever it appears), and its inline
    // form today IS the whole menu document — an exception message carrying it would push the
    // untrusted upload straight into the logs (Inv-12). The catalog row already has the ref for
    // anyone diagnosing one specific failure.
    private const string UnsupportedFormMessage =
        "Artifact reference is not of a form this resolver can read "
        + "(supported: inline:<text>, data:text/plain[;base64],<payload>). "
        + "Object-storage references arrive with #84.";

    private const string OverCapMessage =
        "Artifact content exceeds the 256 KiB menu-text limit.";

    private const string EmptyMessage =
        "Artifact reference resolved to no menu text.";

    /// <inheritdoc />
    public Task<string> ResolveTextAsync(string artifactRef, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Resolve(artifactRef));
    }

    private static string Resolve(string artifactRef)
    {
        if (string.IsNullOrEmpty(artifactRef))
            throw new MenuArtifactUnresolvableException(UnsupportedFormMessage);

        var text =
            artifactRef.StartsWith(InlinePrefix, StringComparison.OrdinalIgnoreCase)
                ? artifactRef[InlinePrefix.Length..]
                : ResolveDataUri(artifactRef);

        if (Encoding.UTF8.GetByteCount(text) > MaxMenuTextBytes)
            throw new MenuArtifactUnresolvableException(OverCapMessage);

        // Never an empty string: an empty menu looks like a successful extraction of nothing and
        // would settle the catalog to DraftReady with no items, which reads to the owner as
        // "your menu is empty" rather than "we could not read your upload".
        if (string.IsNullOrWhiteSpace(text))
            throw new MenuArtifactUnresolvableException(EmptyMessage);

        return text;
    }

    /// <summary>
    /// Parses <c>data:text/plain[;param…][;base64],&lt;payload&gt;</c>.
    ///
    /// <para>Split on the FIRST comma: everything before it is the media type plus its parameters,
    /// everything after is the payload. A comma is legal inside a percent-encoded payload
    /// (<c>Soup, salad</c> is an ordinary menu line), so the split has to be first-comma, not
    /// last. Only the final <c>;base64</c> parameter is honoured; the rest — <c>charset=utf-8</c>
    /// and friends — are ignored rather than refused.</para>
    /// </summary>
    private static string ResolveDataUri(string artifactRef)
    {
        var comma = artifactRef.IndexOf(',');
        if (comma < 0)
            throw new MenuArtifactUnresolvableException(UnsupportedFormMessage);

        var header = artifactRef[..comma];
        var payload = artifactRef[(comma + 1)..];

        var parts = header.Split(';');
        if (!parts[0].Trim().Equals(DataPrefix, StringComparison.OrdinalIgnoreCase))
            throw new MenuArtifactUnresolvableException(UnsupportedFormMessage);

        // RFC 2397: `;base64` is the last parameter when present.
        var isBase64 = parts.Length > 1
            && parts[^1].Trim().Equals(Base64Parameter, StringComparison.OrdinalIgnoreCase);

        return isBase64 ? DecodeBase64(payload) : DecodePercentEncoded(payload);
    }

    private static string DecodeBase64(string encoded)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            // The FormatException message is generic, but it is not passed on as an inner
            // exception either: nothing derived from the reference may reach a log line.
            throw new MenuArtifactUnresolvableException(UnsupportedFormMessage);
        }

        // Checked on the DECODED bytes, before they become a string — this is already the exact
        // count the Python route measures, and it costs nothing.
        if (bytes.Length > MaxMenuTextBytes)
            throw new MenuArtifactUnresolvableException(OverCapMessage);

        return Encoding.UTF8.GetString(bytes);
    }

    private static string DecodePercentEncoded(string encoded)
    {
        try
        {
            return Uri.UnescapeDataString(encoded);
        }
        catch (UriFormatException)
        {
            throw new MenuArtifactUnresolvableException(UnsupportedFormMessage);
        }
    }
}
