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

    /// <summary>
    /// Decoder for every byte→text step here, and the reason it is not the static
    /// <see cref="Encoding.UTF8"/>: that instance carries the REPLACEMENT fallback, so
    /// <c>53-6F-75-70-20-FF-FE-20-24-39</c> comes back as <c>"Soup �� $9"</c> with no
    /// exception. A Latin-1/CP1252 menu base64-encoded without transcoding, or a truncated
    /// multi-byte sequence, would then reach the model as silently altered text: item names carrying
    /// replacement characters, prices possibly corrupted, and all of it landing in
    /// <c>vendor_catalog_items.name</c> for owner review with nothing recording that the bytes were
    /// unreadable. That defeats this resolver's whole purpose — the distinction between "we could
    /// not read your upload" and a successful read. Throwing on invalid bytes makes the first case
    /// say so.
    /// </summary>
    private static readonly UTF8Encoding _strictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

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

    private const string NotUtf8Message =
        "Artifact content is not valid UTF-8 text.";

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

        return DecodeUtf8(bytes);
    }

    /// <summary>
    /// Percent-decodes the payload with the same <see cref="_strictUtf8"/> guarantee the base64 path
    /// gets, rather than delegating to <see cref="Uri.UnescapeDataString"/>.
    ///
    /// <para><b>Measured, because the failure mode is not the obvious one.</b>
    /// <c>Uri.UnescapeDataString</c> does not substitute <c>U+FFFD</c> for an undecodable escape run
    /// — it leaves the escapes as LITERAL TEXT: <c>"Soup%20%FF%FE%20%249"</c> comes back as
    /// <c>"Soup %FF%FE $9"</c>. Milder than mojibake but the same defect, and the one this resolver
    /// exists to prevent: a Latin-1/CP1252 menu percent-encoded without transcoding is silently
    /// handed to the model as unreadable filler instead of being reported as an upload we could not
    /// read.</para>
    ///
    /// <para>Escapes are decoded to BYTES and each maximal run of them is UTF-8 decoded as a unit,
    /// because a multi-byte character is percent-encoded as consecutive escapes. Literal characters
    /// are copied straight through — no re-encode round trip that could itself alter them. A
    /// malformed escape (a stray <c>%</c>, a non-hex pair) stays literal, matching
    /// <c>Uri.UnescapeDataString</c>: refusing an upload over a percent sign in a price would be a
    /// failure no owner could act on.</para>
    /// </summary>
    private static string DecodePercentEncoded(string encoded)
    {
        if (!encoded.Contains('%', StringComparison.Ordinal))
            return encoded;

        var text = new StringBuilder(encoded.Length);
        var escaped = new List<byte>();

        for (var i = 0; i < encoded.Length;)
        {
            if (TryReadEscapedByte(encoded, i, out var value))
            {
                escaped.Add(value);
                i += 3;
                continue;
            }

            FlushEscapedRun(escaped, text);
            text.Append(encoded[i]);
            i++;
        }

        FlushEscapedRun(escaped, text);
        return text.ToString();
    }

    /// <summary>
    /// Recognises exactly <c>%HH</c>. Hex digits are checked by hand rather than through
    /// <c>byte.TryParse(…, NumberStyles.HexNumber)</c>, which also carries
    /// <c>AllowLeadingWhite | AllowTrailingWhite</c> and would therefore read <c>"% 1"</c> as the
    /// byte <c>0x01</c> — inventing a control character out of a stray percent sign that
    /// <see cref="Uri.UnescapeDataString"/> leaves alone.
    /// </summary>
    private static bool TryReadEscapedByte(string encoded, int index, out byte value)
    {
        value = 0;
        if (index + 2 >= encoded.Length || encoded[index] != '%')
            return false;

        var high = HexDigit(encoded[index + 1]);
        var low = HexDigit(encoded[index + 2]);
        if (high < 0 || low < 0)
            return false;

        value = (byte)((high << 4) | low);
        return true;
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    private static void FlushEscapedRun(List<byte> escaped, StringBuilder text)
    {
        if (escaped.Count == 0)
            return;

        text.Append(DecodeUtf8(escaped.ToArray()));
        escaped.Clear();
    }

    /// <summary>
    /// The one byte→text step. <b>The exception is not chained</b> — the house rule in this file is
    /// that nothing derived from the reference reaches a log, and a decoder fallback message names
    /// the offending byte offset in content that IS the uploaded document.
    /// </summary>
    private static string DecodeUtf8(byte[] bytes)
    {
        try
        {
            return _strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new MenuArtifactUnresolvableException(NotUtf8Message);
        }
    }
}
