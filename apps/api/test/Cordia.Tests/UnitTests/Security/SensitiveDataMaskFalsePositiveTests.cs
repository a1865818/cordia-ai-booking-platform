using Cordia.Domain.Security;

namespace Cordia.Tests.UnitTests.Security;

/// <summary>
/// Pins a real, low-rate false positive in the PAN detector rather than leaving it to be
/// rediscovered as a flaky test.
///
/// <para><c>SensitiveDataMask.Sanitize</c> masks any run of 13+ ASCII digits, deliberately failing
/// closed (spec 14). <c>AuditEventSanitizationInterceptor</c> applies it to <c>actor</c>,
/// <c>subject_ref</c> and <c>correlation_id</c>, on the stated assumption that "system-minted
/// values carry no maskable shape and pass byte-identical". That assumption does not hold for
/// **hex** ids: a <c>Guid.NewGuid():N</c> is drawn from <c>0-9a-f</c>, so ~1.8% of them contain a
/// 13-digit run, and ~<b>0.6%</b> (measured over 300k draws — about 1 in 166) additionally embed a
/// Luhn-valid 13-19 digit window and are therefore stored masked.</para>
///
/// <para>The consequence in production is small but real: roughly one audit row in 166 whose subject
/// is a hex id loses the value that links it back to its record. Whether the detector should exempt
/// system-minted id shapes is spec 14's decision, not this branch's — these tests document the
/// behaviour precisely so that decision can be made from evidence.</para>
/// </summary>
[Trait("Category", "Unit")]
public class SensitiveDataMaskFalsePositiveTests
{
    [Fact]
    public void Sanitize_MasksAHexIdWhoseDigitsHappenToBeLuhnValid()
    {
        // A perfectly ordinary GUID-shaped id; nothing about it is card data. Its first 13
        // characters just happen to be digits that pass Luhn, which is all the detector needs.
        const string hexId = "ikey-1234567890128abcdef0123456789abc";

        var sanitized = SensitiveDataMask.Sanitize(hexId);

        Assert.NotEqual(hexId, sanitized);
    }

    [Fact]
    public void Sanitize_LeavesAThirteenDigitRunAloneWhenItFailsLuhn()
    {
        // The detector is narrower than "13 digits": it window-scans for a Luhn-valid 13-19
        // sequence. This is why the false-positive rate is ~0.6% and not ~1.8%.
        const string hexId = "ikey-1234567890123abcdef0123456789abc";

        Assert.Equal(hexId, SensitiveDataMask.Sanitize(hexId));
    }

    [Fact]
    public void Sanitize_LeavesAHexIdWithNoLongDigitRunAlone()
    {
        const string hexId = "ikey-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d";

        Assert.Equal(hexId, SensitiveDataMask.Sanitize(hexId));
    }

    [Fact]
    public void TestIds_AreNeverMasked_HoweverTheGuidFalls()
    {
        // The property the test suite depends on: an id from TestIds survives the interceptor
        // byte-identical, so a test can look its own audit row up by the value it seeded.
        // 5,000 draws puts the ~1.8% raw-GUID failure rate far beyond doubt.
        for (var i = 0; i < 5_000; i++)
        {
            var id = TestIds.New("ikey");
            Assert.Equal(id, SensitiveDataMask.Sanitize(id));
        }
    }

    [Fact]
    public void TestIds_AreStillUnique()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 5_000; i++)
            Assert.True(ids.Add(TestIds.New("ikey")));
    }

    [Fact]
    public void Sanitize_StillMasksAnActualPan_IncludingSeparatedForm()
    {
        // The detector must not have been weakened: these are the cases it exists for.
        Assert.NotEqual("4111111111111111", SensitiveDataMask.Sanitize("4111111111111111"));
        Assert.NotEqual("4111-1111-1111-1111", SensitiveDataMask.Sanitize("4111-1111-1111-1111"));
    }
}
