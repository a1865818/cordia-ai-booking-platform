using System.Text.RegularExpressions;

namespace Cordia.Domain.Security;

/// <summary>
/// Masks PAN-shaped values (13–19 digits, separator-tolerant, Luhn-valid) in
/// free text (Inv-12). Mirrors the foreground detector in
/// <c>cordia_agent/security/redaction.py</c>: non-Luhn digit runs are left
/// intact so reference numbers are not over-redacted. Pure and dependency-free
/// (Domain) so both the audit write path (Application) and the log scrubber
/// (Infrastructure) mask identically.
/// </summary>
public static partial class SensitiveDataMask
{
    public const string MaskToken = "[REDACTED]";

    [GeneratedRegex(@"(?<![\d-])(?:\d[ -]?){12,18}\d(?![\d-])")]
    private static partial Regex PanCandidate();

    public static string Sanitize(string text)
        => PanCandidate().Replace(text, match =>
        {
            var digits = match.Value.Replace(" ", string.Empty).Replace("-", string.Empty);
            return digits.Length is >= 13 and <= 19 && LuhnValid(digits)
                ? MaskToken
                : match.Value;
        });

    private static bool LuhnValid(string digits)
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
