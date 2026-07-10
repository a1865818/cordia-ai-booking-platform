using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cordia.Domain.Security;

/// <summary>
/// Masks sensitive values in free text (Inv-12), mirroring the foreground
/// detector in <c>cordia_agent/security/redaction.py</c>: Luhn-valid PANs
/// (separator-tolerant; runs of 13+ digits are window-scanned or, past 50
/// digits, masked unconditionally — fail closed + DoS cap), CVV-in-context,
/// and SSN/TFN-shaped government identifiers. Space-separated groups longer
/// than 6 digits inside a candidate run signal independent adjacent numbers
/// (two phones, a date-time) and are evaluated per-group. Any digit run that
/// embeds a Luhn-valid 13-19 window is masked (fail closed) so a card padded
/// with adjacent digits cannot escape — at the cost of masking a non-card
/// reference number that coincidentally embeds such a window. Pure and
/// dependency-free (Domain) so the audit write path
/// (Application) and the log scrubber (Api) share one implementation.
///
/// Deliberate asymmetries vs the Python filter: (a) this lane ALWAYS masks
/// SSN/TFN shapes — the audit/log lane is unconditionally conservative and
/// ignores the tenant's mask_government_id opt-out; (b) ASCII digits only —
/// a PAN written in non-ASCII Unicode digits is not detected here (the
/// foreground filter catches those before text ever reaches this lane).
/// </summary>
public static partial class SensitiveDataMask
{
    public const string MaskToken = "[REDACTED]";

    // Window-scanning very long runs is O(n·133); past this length mask
    // unconditionally — strictly more fail-closed and O(1).
    private const int UnconditionalMaskDigits = 50;

    // PAN separator groups are never longer than 6 digits (4-4-4-4, 4-6-5…);
    // a longer space-separated group signals independent adjacent numbers.
    private const int MaxPanGroupDigits = 6;

    // Any contiguous run of >= 13 ASCII digits allowing up to 3 space/dash
    // separators between digits — {0,3} (not the former single {0,1}) so a PAN
    // spoken/typed with a doubled space or dash still forms one candidate run and
    // cannot escape masking; bounded to stay linear (no ReDoS). Longer-than-19
    // runs stay candidates (fail closed).
    [GeneratedRegex(@"[0-9](?:[ -]{0,3}[0-9]){12,}")]
    private static partial Regex PanCandidate();

    // CVV/CVC/security(-)code followed (optionally compactly, e.g. "cvv123") by
    // 3-4 digits — no word boundary required before the digits. "security[ -]?code"
    // matches "security code", "security-code", and "securitycode". The middle
    // group is one separator run plus an optional (filler-word + separator run):
    // collapsing the former S*(word)?S* — two adjacent stars over the same class
    // — to a single leading star makes the match LINEAR, removing the O(n^2)
    // backtracking (ReDoS). This structural bound IS the fix and is parity-equal
    // to the Python lane. Deliberately NO matchTimeout: a wall-clock timeout can
    // spuriously fire under GC/CPU pressure, and RegexMatchTimeoutException would
    // fail OPEN in the Serilog scrubber (enricher exceptions are swallowed and the
    // event emits with the raw value) and hard-fail the SaveChanges/handler lanes.
    [GeneratedRegex(
        @"\b(cvv2?|cvc|security[ -]?code)([\s:=-]*(?:(?:is|was|number|no\.?)[\s:=-]*)?)([0-9]{3,4})\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex CvvInContext();

    // US SSN (###-##-####) and AU TFN (### ### ### with space/dash separators).
    [GeneratedRegex(@"\b[0-9]{3}-[0-9]{2}-[0-9]{4}\b")]
    private static partial Regex Ssn();

    [GeneratedRegex(@"\b[0-9]{3}[ -][0-9]{3}[ -][0-9]{3}\b")]
    private static partial Regex Tfn();

    // Control-plane identifier keys are system-minted and must round-trip
    // byte-for-byte (mirrors _CONTROL_PLANE_* in cordia_agent/security/redaction.py):
    // masking a digit-run inside e.g. operation_id/payload_hash would break
    // idempotency joins and the Inv-2 durability comparison.
    //
    // "_ref" is deliberately NOT a suffix exemption: customer_ref is CLIENT-supplied
    // free text (waitlist enrollment body) that lands in outbox payloads, so a PAN
    // placed there must mask (Inv-12). A typical channel-identity ref (phone/handle)
    // is under 13 digits and never a PAN candidate; the rare 13-15 digit E.164 number
    // that Luhn-coincidentally embeds a card window masks fail-closed - accepted,
    // because no consumer parses customer_ref/identity_ref back OUT of a sanitized
    // payload (waitlist handlers rebuild from the enrollment entity, and recipient
    // resolution must never read identity from a sanitized queue payload).
    // Only the enumerated system-minted ref keys below are exempt; identity_ref is
    // deliberately excluded too (it defaults to the customer-supplied customer_ref
    // at waitlist enrollment).
    private static readonly string[] _controlPlaneKeySuffixes =
        ["_id", "_key", "_hash", "_version"];

    // Bare envelope keys plus the system/provider-minted *_ref keys production emits:
    // provider_ref (provider message/booking references - masking a Luhn-coincidental
    // one would break the sec-16 reconciliation joins), object-store refs, and the
    // streaming chunk/channel refs. Kept identical to redaction.py for parity - pinned
    // by the payload_key_vectors in tools/config/redaction-test-vectors.json.
    private static readonly HashSet<string> _controlPlaneKeys =
    [
        "id", "seq", "ts",
        "provider_ref", "source_artifact_ref", "image_ref",
        "chunk_ref", "audio_chunk_ref", "channel_ref", "audio_ref",
    ];

    /// <summary>
    /// Structure-aware sanitizer for queue/outbox payload JSON (Inv-12), mirroring
    /// Python <c>redact_payload</c>: every string VALUE passes through
    /// <see cref="Sanitize(string)"/> except values under control-plane identifier
    /// keys (<c>*_id</c>/<c>*_key</c>/<c>*_hash</c>/<c>*_version</c>, plus bare
    /// <c>id</c>/<c>seq</c>/<c>ts</c> and an exact-name allowlist of system-minted
    /// <c>*_ref</c> keys), which are preserved verbatim.
    /// Object/array shape and key names are untouched. Non-string scalars pass through
    /// unchanged UNLESS one encodes a maskable value — an unquoted numeric PAN/SSN is masked
    /// to the token string (a JSON number cannot hold the token), fail-closed per Inv-12. When
    /// nothing needed masking the ORIGINAL string is returned byte-identical.
    /// Text that fails to parse as JSON is masked wholesale via
    /// <see cref="Sanitize(string)"/> (fail closed — never store unscreened text).
    /// </summary>
    public static string SanitizeJson(string json)
    {
        if (string.IsNullOrEmpty(json) || CountAsciiDigits(json) < 3)
            return json;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return Sanitize(json);
        }

        var changed = false;
        var sanitized = WalkNode(root, exempt: false, ref changed);
        return changed ? (sanitized?.ToJsonString() ?? "null") : json;
    }

    private static bool IsControlPlaneKey(string key)
    {
        if (_controlPlaneKeys.Contains(key))
            return true;
        foreach (var suffix in _controlPlaneKeySuffixes)
        {
            if (key.EndsWith(suffix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static JsonNode? WalkNode(JsonNode? node, bool exempt, ref bool changed)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    // The exemption applies to the string DIRECTLY under the key; nested
                    // structures under an id-suffixed key are still walked (parity with
                    // redact_payload, which only passes through `isinstance(item, str)`).
                    var childExempt = IsControlPlaneKey(key) && value is JsonValue;
                    var replacement = WalkNode(value, childExempt, ref changed);
                    if (!ReferenceEquals(replacement, value))
                        obj[key] = replacement;
                }
                return node;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var value = arr[i];
                    var replacement = WalkNode(value, exempt: false, ref changed);
                    if (!ReferenceEquals(replacement, value))
                        arr[i] = replacement;
                }
                return node;
            case JsonValue value:
                if (exempt)
                    return node;
                if (value.TryGetValue<string>(out var text))
                {
                    var masked = Sanitize(text);
                    if (ReferenceEquals(masked, text) || masked == text)
                        return node;
                    changed = true;
                    return JsonValue.Create(masked);
                }

                // Non-string scalar (number/bool): a numeric PAN/SSN must not slip through unquoted,
                // e.g. {"card":4111111111111111} (B6). When its textual form contains ANYTHING
                // maskable, replace the WHOLE scalar with the token (a JSON number cannot hold it;
                // never a partial "[REDACTED].45" that leaks the fractional digits beside it). Bools
                // and ordinary numbers have no 13+ digit run / CVV context, so they pass through.
                // DELIBERATE TYPE-CHANGE TRADEOFF: a 13-19 digit number that coincides with a
                // Luhn-valid window (a rare epoch-millis timestamp or a large minor-unit total) is
                // masked too, turning the JSON number into the "[REDACTED]" string. Fail-closed
                // (Inv-12): over-masking a number beats leaking a card, symmetric with the string
                // lane. A typed-numeric consumer (e.g. JobPayload.GetInt32OrNull) reads the type
                // change as "absent"; numeric envelope fields are small and control-plane ids are
                // exempt, so a 13+ digit maskable numeric field is not a normal shape.
                var raw = value.ToString();
                if (Sanitize(raw) == raw)
                    return node;
                changed = true;
                return JsonValue.Create(MaskToken);
            default:
                return node;
        }
    }

    public static string Sanitize(string text)
    {
        // Fast path: nothing maskable exists with fewer than 3 ASCII digits.
        if (CountAsciiDigits(text) < 3)
            return text;

        var output = PanCandidate().Replace(text, match => MaskCandidateRun(match.Value));
        output = CvvInContext().Replace(output, match =>
            $"{match.Groups[1].Value}{match.Groups[2].Value}{MaskToken}");
        output = Ssn().Replace(output, MaskToken);
        output = Tfn().Replace(output, MaskToken);
        return output;
    }

    private static int CountAsciiDigits(string text)
    {
        var count = 0;
        foreach (var ch in text)
        {
            if (ch is >= '0' and <= '9' && ++count >= 3)
                return count;
        }
        return count;
    }

    /// <summary>
    /// Masks a single separator-tolerant candidate run — or, when it contains
    /// a space-separated group longer than 6 digits (independent adjacent
    /// numbers, not PAN grouping), evaluates each group on its own.
    /// </summary>
    private static string MaskCandidateRun(string run)
    {
        var groups = run.Split(' ');
        if (groups.Length > 1)
        {
            var digitLengths = new int[groups.Length];
            var hasLongGroup = false;
            for (var i = 0; i < groups.Length; i++)
            {
                digitLengths[i] = groups[i].Replace("-", string.Empty).Length;
                hasLongGroup |= digitLengths[i] > MaxPanGroupDigits;
            }
            if (hasLongGroup)
            {
                for (var i = 0; i < groups.Length; i++)
                {
                    if (digitLengths[i] >= 13 && ContainsPan(groups[i].Replace("-", string.Empty)))
                        groups[i] = MaskToken;
                }
                return string.Join(' ', groups);
            }
        }

        var digits = run.Replace(" ", string.Empty).Replace("-", string.Empty);
        return ContainsPan(digits) ? MaskToken : run;
    }

    /// <summary>
    /// True iff <paramref name="digits"/> contains a Luhn-valid 13-19 digit
    /// window. EVERY run of >= 13 digits is window-scanned (sizes 19..13), not
    /// just runs longer than 19: a PAN padded with adjacent digits (e.g. a
    /// 16-digit card with one trailing digit) fails a whole-run Luhn check but
    /// must still be masked (Inv-12 fail-closed — card data must never escape).
    /// This deliberately over-masks: a non-Luhn 14-19 digit reference number
    /// that happens to embed a Luhn-valid window is masked too. Runs past
    /// <see cref="UnconditionalMaskDigits"/> mask without scanning (DoS cap).
    /// </summary>
    private static bool ContainsPan(string digits)
    {
        if (digits.Length > UnconditionalMaskDigits)
            return true;

        for (var size = Math.Min(19, digits.Length); size >= 13; size--)
        {
            for (var start = 0; start <= digits.Length - size; start++)
            {
                if (LuhnValid(digits.AsSpan(start, size)))
                    return true;
            }
        }
        return false;
    }

    private static bool LuhnValid(ReadOnlySpan<char> digits)
    {
        var total = 0;
        var alternate = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var value = digits[i] - '0';
            if (alternate)
            {
                value *= 2;
                if (value > 9)
                    value -= 9;
            }
            total += value;
            alternate = !alternate;
        }
        return total % 10 == 0;
    }
}
