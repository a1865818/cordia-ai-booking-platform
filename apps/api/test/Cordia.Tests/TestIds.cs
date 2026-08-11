namespace Cordia.Tests;

/// <summary>
/// Unique test ids that cannot be mistaken for a card number.
///
/// <para><b>Why this exists.</b> <c>AuditEventSanitizationInterceptor</c> masks the free-text
/// identity columns (<c>actor</c>, <c>subject_ref</c>, <c>correlation_id</c>) through
/// <c>SensitiveDataMask.Sanitize</c>, whose PAN detector window-scans any run of 13+ ASCII digits
/// for a Luhn-valid 13-19 sequence. Its comment assumes "system-minted values carry no maskable
/// shape and pass byte-identical" — which does not hold for a hex id: <c>Guid.NewGuid():N</c> is
/// drawn from <c>0-9a-f</c>, so ~1.8% contain a long digit run and <b>~0.6% (about 1 in 166,
/// measured over 300k draws)</b> are Luhn-valid and stored masked.</para>
///
/// <para>A test that seeds such an id and then looks the audit row up by its raw value therefore
/// fails roughly one run in 166, at random, with no relation to isolation or ordering —
/// which is exactly how <c>RetentionSweepTests</c> presented: failing in a full run and passing
/// six times out of six in isolation. It was never an ordering problem.</para>
///
/// <para>Interleaving a non-digit every few characters makes the longest possible digit run
/// shorter than the detector's threshold, so the id is unique, still opaque, and deterministic
/// in the only way that matters here: it can never be masked. See
/// <c>SensitiveDataMaskFalsePositiveTests</c>, which pins the underlying behaviour.</para>
/// </summary>
public static class TestIds
{
    /// <summary>
    /// A unique id of the form <c>{prefix}-a1b2xc3d4x...</c>: hex in groups of four joined by the
    /// letter <c>x</c>, so no digit run can reach 13 even when every hex character happens to be a
    /// digit. The separator is a <b>letter</b>, not a hyphen, for the reason in the remarks.
    /// </summary>
    /// <remarks>
    /// The separator matters as much as the grouping: the detector tolerates up to three
    /// space/dash separators <i>between</i> digits, so a single hyphen would not break a run.
    /// Groups of four keep runs at most 4 digits long even across several separators — the same
    /// reason a real PAN in <c>4-4-4-4</c> form is still detected but a group of four alone is not.
    /// </remarks>
    public static string New(string prefix)
    {
        var hex = Guid.NewGuid().ToString("N");
        var groups = new string[8];
        for (var i = 0; i < 8; i++)
            groups[i] = hex.Substring(i * 4, 4);

        // Letters between groups, not just hyphens: 4-digit groups joined by hyphens are exactly
        // the shape the separator-tolerant detector is built to catch.
        return $"{prefix}-{string.Join("x", groups)}";
    }
}
