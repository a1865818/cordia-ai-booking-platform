using System.Text;
using System.Text.Json;
using Cordia.Domain.Security;

namespace Cordia.Tests.UnitTests.Security;

/// <summary>
/// Encoded <c>data:</c> artifact references (#95 follow-up, Inv-12).
///
/// <para><b>What #95 fixed and what it missed.</b> Taking <c>source_artifact_ref</c> off the
/// control-plane exemption list made the masker LOOK at the reference. It did not make it SEE
/// it. A <c>data:</c> reference carries its document in a transport encoding, and every
/// detector scanned the encoding: <c>card 4111111111111111</c> base64-encodes to
/// <c>Y2FyZCA0MTExMTExMTExMTExMTEx</c>, whose only digits are a single <c>2</c> and a single
/// <c>0</c> with letters between them. That clears neither the three-digit fast path nor the
/// 13-digit candidate run, so the PAN still reached <c>outbox_messages.payload</c> verbatim —
/// through a choke point that was executing and finding nothing.</para>
///
/// <para>Percent-encoding never had that problem: <c>%XX</c> escapes only non-digit bytes, so
/// the card's digits stayed literal ASCII and matched. It had the opposite one — the mask
/// swallowed the <c>2</c> and <c>0</c> of a neighbouring <c>%20</c> into the digit run and
/// emitted <c>card%[REDACTED]</c>, a broken escape the resolver can no longer decode. Both
/// forms are asserted here, and they assert different things.</para>
/// </summary>
[Trait("Category", "Unit")]
public class SensitiveDataMaskDataUriTests
{
    private const string Pan = "4111111111111111";
    private const string Mask = SensitiveDataMask.MaskToken;

    private static string B64Ref(string text, string header = "data:text/plain;base64") =>
        $"{header},{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}";

    private static string DecodeB64Ref(string reference) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(reference.Split(',', 2)[1]));

    // ── The hole ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The load-bearing one. Reverting <c>TrySanitizeDataUri</c> reds exactly this family.
    /// </summary>
    [Fact]
    public void Base64DataRef_MasksThePanInsideTheDecodedPayload()
    {
        var reference = B64Ref($"Tasting menu 120.00 card {Pan}");

        var masked = SensitiveDataMask.Sanitize(reference);

        Assert.NotEqual(reference, masked);
        var decoded = DecodeB64Ref(masked);
        Assert.DoesNotContain(Pan, decoded, StringComparison.Ordinal);
        Assert.Contains(Mask, decoded, StringComparison.Ordinal);
    }

    /// <summary>
    /// Masking must replace the card and nothing else. Asserting the whole decoded string
    /// rather than "PAN absent" is deliberate: a masker that replaced the entire reference
    /// with the token would satisfy the weaker check and silently break every menu upload,
    /// because the consumer reads this reference straight back out.
    /// </summary>
    [Fact]
    public void Base64DataRef_KeepsTheMenuAroundThePan()
    {
        var reference = B64Ref($"Tasting menu 120.00 card {Pan} on account");

        var decoded = DecodeB64Ref(SensitiveDataMask.Sanitize(reference));

        Assert.Equal($"Tasting menu 120.00 card {Mask} on account", decoded);
    }

    /// <summary>
    /// A clean menu comes back BYTE-IDENTICAL. A decode/re-encode that normalised padding,
    /// whitespace or line breaks would corrupt every upload that had nothing wrong with it.
    /// </summary>
    [Fact]
    public void Base64DataRef_WithoutAPan_RoundTripsByteIdentical()
    {
        var reference = B64Ref("Soup 8.50, Salad 12.00, Set menu for 2 at 65.00, table 12");

        Assert.Equal(reference, SensitiveDataMask.Sanitize(reference));
    }

    /// <summary>
    /// RFC 2397 parameters before <c>;base64</c> are tolerated — the header-parsing path, not
    /// just the decoder. <c>charset=utf-8;base64</c> is the commonly-emitted form.
    /// </summary>
    [Fact]
    public void Base64DataRef_WithCharsetParameter_IsStillDecoded()
    {
        var reference = B64Ref($"card {Pan}", "data:text/plain;charset=utf-8;base64");

        Assert.DoesNotContain(Pan, DecodeB64Ref(SensitiveDataMask.Sanitize(reference)));
    }

    // ── The fast path was part of the hole ─────────────────────────────────────────

    /// <summary>
    /// <c>SanitizeJson</c> bails out below three ASCII digits — and base64 re-alphabets a
    /// document's digits away entirely. A bare PAN encodes to <c>NDExMTExMTExMTExMTExMQ==</c>,
    /// which contains no digit at all, so the ONLY two in the whole JSON document are the
    /// <c>6</c> and <c>4</c> of the literal word "base64" in the header. Below the threshold,
    /// the guard returns the payload untouched and the card is never offered to a detector.
    ///
    /// <para>This is why the fast path needed a <c>data:</c> escape hatch and not just a
    /// higher threshold: the digit count of an encoded document says nothing about the digit
    /// count of the document.</para>
    /// </summary>
    [Fact]
    public void SanitizeJson_DoesNotTrustTheDigitFastPath_ForAnEncodedRef()
    {
        var reference = B64Ref(Pan);
        var json = $$"""{"source_artifact_ref":"{{reference}}"}""";

        // Both from "base64"; the encoded card contributes none.
        Assert.Equal(2, json.Count(char.IsAsciiDigit));

        var sanitized = SensitiveDataMask.SanitizeJson(json);

        Assert.NotEqual(json, sanitized);
        var roundTripped = JsonDocument.Parse(sanitized)
            .RootElement.GetProperty("source_artifact_ref").GetString()!;
        Assert.DoesNotContain(Pan, DecodeB64Ref(roundTripped), StringComparison.Ordinal);
    }

    // ── Percent-encoding: detected before, but mangled on the way out ──────────────

    [Fact]
    public void PercentEncodedDataRef_StaysResolvableAfterMasking()
    {
        var reference = $"data:text/plain,card%20{Pan}";

        var masked = SensitiveDataMask.Sanitize(reference);

        Assert.DoesNotContain(Pan, masked, StringComparison.Ordinal);
        // The pre-fix output was `data:text/plain,card%[REDACTED]` — a dangling escape.
        Assert.DoesNotContain($"%{Mask}", masked, StringComparison.Ordinal);
        Assert.Equal($"card {Mask}", masked.Split(',', 2)[1]);
    }

    // ── Fall-through is not a skip ─────────────────────────────────────────────────

    /// <summary>
    /// A <c>;base64</c> header whose payload is not valid base64 must not be SKIPPED: the raw
    /// text still takes the ordinary passes, so a literal PAN in it still masks. This is what
    /// keeps the fix from ever leaving a reference less masked than it was before.
    /// </summary>
    [Fact]
    public void UndecodableBase64_FallsThroughToTheRawPasses()
    {
        var reference = $"data:text/plain;base64,card {Pan}";

        var masked = SensitiveDataMask.Sanitize(reference);

        Assert.DoesNotContain(Pan, masked, StringComparison.Ordinal);
        Assert.Contains(Mask, masked, StringComparison.Ordinal);
    }

    /// <summary>Valid base64, undecodable as text: nothing to scan, nothing to over-mask.</summary>
    [Fact]
    public void Base64PayloadThatIsNotUtf8Text_IsLeftAlone()
    {
        const string reference = "data:text/plain;base64,//4=";

        Assert.Equal(reference, SensitiveDataMask.Sanitize(reference));
    }

    /// <summary>
    /// A reference whose decoded content is itself a <c>data:</c> string must terminate: the
    /// decoded text takes the plain passes, never the <c>data:</c> branch again. Without that
    /// split this recurses until the stack goes.
    /// </summary>
    [Fact]
    public void NestedDataRef_Terminates()
    {
        var reference = B64Ref(B64Ref($"card {Pan}"));

        // The inner reference is opaque base64 to the plain passes, so nothing matches and the
        // outer value round-trips. The assertion that matters is that this call RETURNS.
        Assert.Equal(reference, SensitiveDataMask.Sanitize(reference));
    }

    /// <summary>Not a data: reference at all — the ordinary passes still apply.</summary>
    [Fact]
    public void OrdinaryTextIsUnaffectedByTheDataUriBranch()
    {
        Assert.Equal($"my card is {Mask} thanks", SensitiveDataMask.Sanitize($"my card is {Pan} thanks"));
        Assert.Equal("party of 123 people", SensitiveDataMask.Sanitize("party of 123 people"));
    }
}
