using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cordia.Domain.Interfaces;

namespace Cordia.Infrastructure.Idempotency;

/// <summary>
/// Implements the canon.v1 canonical request hasher (spec 04 §5).
///
/// Algorithm (applied recursively):
///   Object  — omit null-valued properties; omit properties whose canonicalized value is null,
///             an empty object {}, or an empty array [] (empty optional collections do not change
///             the hash — spec §4.2 "preorder_items = []" default); sort remaining keys by
///             Unicode ordinal code point after applying NFC to the key (symmetry with value
///             normalization); recurse into each value.
///   String  — apply Unicode NFC; trim leading/trailing whitespace; collapse internal whitespace
///             runs to a single space; do NOT change case (semantic-preserving).
///             Exception: ISO-8601 date-time strings with an offset are parsed to UTC and
///             re-emitted as "yyyy-MM-ddTHH:mm:ss.fffZ".
///             Canonical timestamp resolution is milliseconds; sub-millisecond precision is
///             truncated (floored), never rounded, so the canonical form never crosses a
///             second/minute/hour boundary.
///   Number  — canonicalized so that 500, 500.0, and 5e2 produce the same output.
///             Integer path: if the JSON number (including exponent forms like 1e30) resolves to
///             an integer, normalize via BigInteger to a canonical decimal integer string.
///             Fractional path: parse to decimal (G29) for shortest round-trip without trailing
///             zeros. Falls back to raw text only if both conversions fail.
///   Array   — canonicalize each element, then sort. Arrays of objects: sort by natural key
///             (item_id, then modifier_id) when present, falling back to the element's full
///             canonical JSON string (total order — List.Sort instability cannot affect output).
///             Scalar arrays: sort by canonical value (ordinal), same tiebreaker applies.
///   Encoding — compact UTF-8 JSON, then SHA-256 → lowercase hex (64 chars).
///   Null / empty payload → hashes the canonical empty object "{}" (deterministic).
/// </summary>
public sealed class CanonicalRequestHasher : ICanonicalRequestHasher
{
    private const string VersionLabel = "canon.v1";

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        WriteIndented = false,
    };

    /// <inheritdoc/>
    public CanonicalHash Hash(JsonNode? payload)
    {
        // Null / empty payload → canonical empty object.
        var root = payload ?? JsonNode.Parse("{}")!;

        var canonical = Canonicalize(root);

        // Serialize to compact UTF-8 JSON.
        var json = canonical?.ToJsonString(CompactOptions) ?? "{}";
        var bytes = Encoding.UTF8.GetBytes(json);

        // SHA-256 → lowercase hex.
        var hashBytes = SHA256.HashData(bytes);
        var hex = Convert.ToHexString(hashBytes).ToLowerInvariant();

        return new CanonicalHash(VersionLabel, hex);
    }

    // -------------------------------------------------------------------------
    // Recursive canonicalization
    // -------------------------------------------------------------------------

    private static JsonNode? Canonicalize(JsonNode? node) => node switch
    {
        JsonObject obj => CanonicalizeObject(obj),
        JsonArray arr => CanonicalizeArray(arr),
        JsonValue val => CanonicalizeValue(val),
        _ => null, // explicit null node → omit (handled by object key filter)
    };

    private static JsonObject CanonicalizeObject(JsonObject obj)
    {
        // 1. Collect non-null properties.
        // 2. Apply NFC to keys before sorting (symmetry with value NFC normalization).
        // 3. Sort keys by Unicode ordinal code point (StringComparer.Ordinal).
        // 4. Recurse into values; omit null, empty-object, and empty-array results
        //    (empty optional collections do not change the hash — spec §4.2).
        var sorted = new JsonObject();
        foreach (var kvp in obj.OrderBy(
            p => p.Key.Normalize(NormalizationForm.FormC),
            StringComparer.Ordinal))
        {
            if (kvp.Value is null)
                continue; // omit explicit JSON null (absent and null are identical in canon.v1)

            var canonical = Canonicalize(kvp.Value);
            if (canonical is null)
                continue; // value canonicalized to null → omit

            // Omit empty object {} and empty array [] (empty optional collections do not change the hash).
            if (canonical is JsonObject co && co.Count == 0)
                continue;
            if (canonical is JsonArray ca && ca.Count == 0)
                continue;

            sorted.Add(kvp.Key.Normalize(NormalizationForm.FormC), canonical);
        }
        return sorted;
    }

    private static JsonArray CanonicalizeArray(JsonArray arr)
    {
        // Canonicalize every element first.
        var items = new List<JsonNode?>(arr.Count);
        foreach (var element in arr)
            items.Add(Canonicalize(element));

        // Determine whether elements are objects with natural keys.
        bool allObjects = items.All(e => e is JsonObject);

        if (allObjects && items.Count > 0)
        {
            // Sort by natural key: item_id first, then modifier_id, then full canonical JSON string.
            // The full-canonical-JSON tiebreaker provides a total order so List.Sort instability
            // cannot affect output.
            items.Sort((a, b) =>
            {
                var aObj = (JsonObject)a!;
                var bObj = (JsonObject)b!;

                // Fix: read natural-key fields defensively — string nodes yield their value,
                // non-string nodes (e.g. numeric item_id) yield the canonical JSON text,
                // and missing keys yield empty string. This prevents GetValue<string>() from
                // throwing when item_id/modifier_id is a JSON number.
                var aItemId = GetNaturalKeyString(aObj["item_id"]);
                var bItemId = GetNaturalKeyString(bObj["item_id"]);
                int cmp = string.Compare(aItemId, bItemId, StringComparison.Ordinal);
                if (cmp != 0) return cmp;

                var aModId = GetNaturalKeyString(aObj["modifier_id"]);
                var bModId = GetNaturalKeyString(bObj["modifier_id"]);
                cmp = string.Compare(aModId, bModId, StringComparison.Ordinal);
                if (cmp != 0) return cmp;

                // Fall back to full canonical JSON string for complete determinism.
                var aJson = a!.ToJsonString(CompactOptions);
                var bJson = b!.ToJsonString(CompactOptions);
                return string.Compare(aJson, bJson, StringComparison.Ordinal);
            });
        }
        else
        {
            // Scalar (or mixed) array: sort by canonical value string (ordinal).
            // The canonical JSON string tiebreaker provides a total order so List.Sort instability
            // cannot affect output.
            items.Sort((a, b) =>
            {
                var aStr = a?.ToJsonString(CompactOptions) ?? "null";
                var bStr = b?.ToJsonString(CompactOptions) ?? "null";
                return string.Compare(aStr, bStr, StringComparison.Ordinal);
            });
        }

        var result = new JsonArray();
        foreach (var item in items)
            result.Add(item?.DeepClone());
        return result;
    }

    /// <summary>
    /// Reads a natural-key field (item_id / modifier_id) defensively.
    /// If the node is a JSON string (either stored as JsonElement or as a CLR string after
    /// canonicalization), returns its string value.
    /// If the node is any other JSON value (e.g. a number), returns its canonical JSON text.
    /// If the node is absent (null), returns empty string.
    /// Never throws — the comparer must not throw inside List.Sort.
    /// </summary>
    private static string GetNaturalKeyString(JsonNode? node)
    {
        if (node is null) return string.Empty;

        if (node is JsonValue val)
        {
            // After canonicalization, string values are stored as CLR strings
            // (JsonValue.Create(string)), not as JsonElement. Try string first.
            if (val.TryGetValue<string>(out var strValue))
                return strValue ?? string.Empty;

            // Original input: may be stored as JsonElement.
            if (val.TryGetValue<JsonElement>(out var elem)
                && elem.ValueKind == JsonValueKind.String)
                return elem.GetString() ?? string.Empty;
        }

        // Non-string or non-value node: fall back to canonical JSON text.
        return node.ToJsonString(CompactOptions);
    }

    private static JsonNode CanonicalizeValue(JsonValue val)
    {
        var elem = val.GetValue<JsonElement>();

        return elem.ValueKind switch
        {
            JsonValueKind.String => CanonicalizeString(elem.GetString() ?? string.Empty),
            JsonValueKind.Number => CanonicalizeNumber(elem),
            // True / False / Null pass through unchanged.
            _ => JsonValue.Create(elem)!,
        };
    }

    private static JsonNode CanonicalizeString(string raw)
    {
        // Try to detect ISO-8601 date-time with an offset/Z suffix.
        if (TryNormalizeTimestamp(raw, out var normalized))
            return JsonValue.Create(normalized)!;

        // General string: NFC, trim, collapse internal whitespace.
        var s = raw.Normalize(NormalizationForm.FormC);
        s = s.Trim();
        s = CollapseWhitespace(s);
        return JsonValue.Create(s)!;
    }

    private static bool TryNormalizeTimestamp(string raw, out string result)
    {
        result = string.Empty;

        // Quick pre-filter: must contain 'T' (date+time separator) and either 'Z'/'z' or '+'/'-'
        // in the time portion to indicate an offset. This avoids expensive parse attempts on plain
        // strings like "absent", "declined", UUIDs, or bare date strings ("2026-06-15").
        // Fix: accept lowercase 'z' as well as uppercase 'Z' — DateTimeOffset.TryParse accepts
        // both, but only the gate was checking for uppercase.
        var tIdx = raw.IndexOf('T', StringComparison.Ordinal);
        if (tIdx < 0) return false;

        var afterT = raw.AsSpan(tIdx + 1);
        bool hasOffset = afterT.Contains('Z')
            || afterT.Contains('z')
            || afterT.Contains('+')
            || afterT.Contains('-');
        if (!hasOffset) return false;

        if (!DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var dto))
        {
            return false;
        }

        // Convert to UTC, then TRUNCATE (floor) to whole milliseconds before formatting.
        // Using ToString("...fff") alone would ROUND, which can cross second/minute/hour
        // boundaries (e.g. ...59.9995Z → next minute) and silently merge distinct instants.
        // Python/Pydantic emits microsecond precision (6 fractional digits), so truncation
        // is necessary on every real-world payload.
        // Canonical timestamp resolution is milliseconds; sub-ms precision is truncated (floored).
        var utc = dto.ToUniversalTime();
        var truncated = utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMillisecond));
        result = truncated.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        return true;
    }

    private static JsonNode CanonicalizeNumber(JsonElement elem)
    {
        var raw = elem.GetRawText();

        // Integer path: if the value (including exponent forms like 5e2, 1e30) resolves to a
        // whole integer, normalize via BigInteger to a canonical decimal integer string.
        // This ensures 5e2 == 500 == 500.0 and handles values beyond decimal range (e.g. 1e30).
        if (TryParseAsBigInteger(raw, out var bigInt))
            return JsonNode.Parse(bigInt.ToString(CultureInfo.InvariantCulture))!;

        // Fractional path: parse to decimal for canonical round-trip (500.5 stays 500.5).
        // Use the raw text to avoid floating-point precision loss.
        if (!decimal.TryParse(
                raw,
                NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var d))
        {
            // Fallback: return as-is (should not happen for valid JSON numbers).
            return JsonValue.Create(elem)!;
        }

        // Remove trailing zeros after decimal point; keep integers as integers.
        // G29 gives shortest round-trip representation without trailing zeros.
        var str = d.ToString("G29", CultureInfo.InvariantCulture);

        if (decimal.TryParse(str, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var canonical))
            return JsonValue.Create(canonical)!;

        return JsonValue.Create(d)!;
    }

    /// <summary>
    /// Attempts to interpret a raw JSON number string as a whole integer via BigInteger.
    /// Handles plain integers ("500"), decimal integers ("500.0"), and exponent forms ("5e2", "1e30").
    /// For exponent forms that exceed decimal range (e.g. "1e30"), the value is computed exactly
    /// by parsing the mantissa and exponent separately to avoid double precision loss.
    /// Returns false for fractional values (e.g. "500.5", "1.5e2").
    /// </summary>
    private static bool TryParseAsBigInteger(string raw, out BigInteger result)
    {
        result = BigInteger.Zero;

        // First try direct BigInteger parse (handles plain integers like "500", "1000000000000000000000000000000").
        if (BigInteger.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
            return true;

        // Try via decimal first to handle "500.0", "5e2", and in-range integers.
        if (decimal.TryParse(raw, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d))
        {
            // Check fractional part — if non-zero, this is not an integer.
            if (d != Math.Floor(d)) return false;

            try
            {
                result = new BigInteger(d);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // decimal range exceeded — parse mantissa and exponent separately to avoid
        // double precision loss (e.g. new BigInteger(1e30) != BigInteger.Pow(10, 30)).
        return TryParseExponentFormAsBigInteger(raw, out result);
    }

    /// <summary>
    /// Parses a JSON number in exponent form (e.g. "1e30", "25e28") that exceeds decimal range
    /// into an exact BigInteger, without going through double (which loses precision).
    /// Splits on 'e'/'E', checks that the mantissa has no fractional part after scaling,
    /// then computes mantissa * 10^exponent using BigInteger arithmetic.
    /// Returns false for fractional results or unparseable inputs.
    /// </summary>
    private static bool TryParseExponentFormAsBigInteger(string raw, out BigInteger result)
    {
        result = BigInteger.Zero;

        var eIdx = raw.IndexOfAny(['e', 'E']);
        if (eIdx < 0) return false;

        var mantissaStr = raw.AsSpan(0, eIdx);
        var expStr = raw.AsSpan(eIdx + 1);

        if (!int.TryParse(expStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exponent))
            return false;

        // Parse mantissa — may have a decimal point (e.g. "1.5e2").
        var dotIdx = mantissaStr.IndexOf('.');
        int mantissaScale = 0; // number of digits after the decimal point in mantissa
        BigInteger mantissaInt;

        if (dotIdx >= 0)
        {
            // Separate integer and fractional parts.
            var intPart = mantissaStr[..dotIdx];
            var fracPart = mantissaStr[(dotIdx + 1)..];
            mantissaScale = fracPart.Length;

            // Remove trailing zeros from fractional part to minimize mantissaScale.
            int effectiveFracLen = fracPart.Length;
            while (effectiveFracLen > 0 && fracPart[effectiveFracLen - 1] == '0')
                effectiveFracLen--;
            fracPart = fracPart[..effectiveFracLen];
            mantissaScale = effectiveFracLen;

            // Combine int and frac parts into a single integer: intPart * 10^mantissaScale + frac.
            if (!BigInteger.TryParse(intPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intPartBig))
                return false;
            BigInteger fracPartBig = BigInteger.Zero;
            if (mantissaScale > 0 &&
                !BigInteger.TryParse(fracPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out fracPartBig))
                return false;

            // Derive the sign from the raw mantissa string, NOT from intPartBig.
            // When intPart is "-0" (e.g. mantissa "-0.5"), BigInteger.TryParse("-0")
            // yields 0, so intPartBig < 0 is false and the sign would be lost.
            var negative = mantissaStr.TrimStart().StartsWith("-", StringComparison.Ordinal);
            var sign = negative ? BigInteger.MinusOne : BigInteger.One;
            mantissaInt = BigInteger.Abs(intPartBig) * BigInteger.Pow(10, mantissaScale) + fracPartBig;
            mantissaInt *= sign;
        }
        else
        {
            if (!BigInteger.TryParse(mantissaStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out mantissaInt))
                return false;
        }

        // Net exponent = exponent - mantissaScale.
        int netExp = exponent - mantissaScale;

        if (netExp < 0)
            return false; // fractional result — not an integer

        result = mantissaInt * BigInteger.Pow(10, netExp);
        return true;
    }

    private static string CollapseWhitespace(string s)
    {
        if (s.Length == 0) return s;

        var sb = new StringBuilder(s.Length);
        bool inSpace = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!inSpace)
                {
                    sb.Append(' ');
                    inSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                inSpace = false;
            }
        }
        return sb.ToString();
    }
}
