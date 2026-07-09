using System.Text.RegularExpressions;

namespace Cordia.Domain.Security;

/// <summary>
/// Masks sensitive values in free text (Inv-12), mirroring the foreground
/// detector in <c>cordia_agent/security/redaction.py</c>: Luhn-valid PANs
/// (separator-tolerant; runs longer than 19 digits are window-scanned or,
/// past 50 digits, masked unconditionally — fail closed + DoS cap),
/// CVV-in-context, and SSN/TFN-shaped government identifiers. Space-separated
/// groups longer than 6 digits inside a candidate run signal independent
/// adjacent numbers (two phones, a date-time) and are evaluated per-group.
/// Non-Luhn 13-19 digit runs are left intact so reference numbers are not
/// over-redacted. Pure and dependency-free (Domain) so the audit write path
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

    // Any contiguous run of >= 13 ASCII digits allowing single space/dash
    // separators. Longer-than-19 runs stay candidates (fail closed).
    [GeneratedRegex(@"[0-9](?:[ -]?[0-9]){12,}")]
    private static partial Regex PanCandidate();

    // CVV/CVC/security code followed (optionally compactly, e.g. "cvv123")
    // by 3-4 digits — no word boundary required before the digits.
    [GeneratedRegex(@"\b(cvv2?|cvc|security code)([\s:=-]*(?:is|was|number|no\.?)?[\s:=-]*)([0-9]{3,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex CvvInContext();

    // US SSN (###-##-####) and AU TFN (### ### ### with space/dash separators).
    [GeneratedRegex(@"\b[0-9]{3}-[0-9]{2}-[0-9]{4}\b")]
    private static partial Regex Ssn();

    [GeneratedRegex(@"\b[0-9]{3}[ -][0-9]{3}[ -][0-9]{3}\b")]
    private static partial Regex Tfn();

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
    /// True iff <paramref name="digits"/> is, or contains, a Luhn-valid 13-19
    /// digit window. Exact-length runs (13-19) are checked as a whole —
    /// keeping non-Luhn reference numbers intact — while longer runs are
    /// window-scanned and masked wholesale when any window passes (Inv-12
    /// fail-closed: a PAN concatenated with adjacent digits must not escape).
    /// Runs past <see cref="UnconditionalMaskDigits"/> mask without scanning.
    /// </summary>
    private static bool ContainsPan(string digits)
    {
        if (digits.Length <= 19)
            return LuhnValid(digits.AsSpan());

        if (digits.Length > UnconditionalMaskDigits)
            return true;

        for (var size = 19; size >= 13; size--)
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
