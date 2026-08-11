using Cordia.Domain.Bookings;

namespace Cordia.Tests.UnitTests.ManageBooking;

/// <summary>
/// Phone comparison for booking discovery.
///
/// The stored number lives inside an immutable, hashed canonical payload, so it can never be
/// rewritten into a canonical form after the fact. Comparison therefore has to normalise both
/// sides — and it has to do so WITHOUT guessing a country, because that guess would silently
/// match one guest's booking to another guest's number.
/// </summary>
[Trait("Category", "Unit")]
public class PhoneMatchingTests
{
    [Theory]
    [InlineData("+61 400 111 222", "+61400111222")]
    [InlineData("+61-400-111-222", "+61400111222")]
    [InlineData("(+61) 400 111 222", "+61400111222")]
    [InlineData("+61.400.111.222", "+61400111222")]
    [InlineData("  +61400111222  ", "+61400111222")]
    public void FormattingDifferencesStillMatch(string stored, string searched)
    {
        // The whole point: an operator who typed separators has not created an unfindable booking.
        Assert.True(PhoneMatching.Matches(stored, searched));
        Assert.True(PhoneMatching.Matches(searched, stored));
    }

    [Fact]
    public void DoesNotGuessACountryCode()
    {
        // 0400111222 and +61400111222 ARE the same Australian number, but proving that needs the
        // tenant's country, which the platform records nowhere. Guessing +61 would, in any
        // country whose national format collides, match a stranger's booking. The console warns
        // at capture instead — see the Guest phone field.
        Assert.False(PhoneMatching.Matches("0400111222", "+61400111222"));
    }

    [Theory]
    [InlineData("+61400111222", "+61400111223")]
    [InlineData("+61400111222", "+6140011122")]
    [InlineData("+1400111222", "+61400111222")]
    public void DifferentNumbersDoNotMatch(string a, string b)
    {
        Assert.False(PhoneMatching.Matches(a, b));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData("---", "---")]
    [InlineData("+", "+")]
    public void AbsentNeverMatchesAbsent(string? a, string? b)
    {
        // Two bookings with no phone must not be "the same guest". A punctuation-only or
        // plus-only value carries no digits and is treated as absent for the same reason.
        Assert.False(PhoneMatching.Matches(a, b));
    }

    [Theory]
    [InlineData("+61 400 111 222", "+61400111222")]
    [InlineData("0400 111 222", "0400111222")]
    [InlineData("(02) 9999 1234", "0299991234")]
    public void NormaliseStripsFormattingAndKeepsALeadingPlus(string raw, string expected)
    {
        Assert.Equal(expected, PhoneMatching.Normalise(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+")]
    [InlineData("()- .")]
    public void NormaliseReturnsNullWhenThereAreNoDigits(string? raw)
    {
        Assert.Null(PhoneMatching.Normalise(raw));
    }

    // ── The raw-length ceiling, pinned on BOTH sides ────────────────────────────────────────────
    //
    // `normalisePhone` in apps/web/src/features/bookings/buildSubmission.ts carries the identical
    // bound and the identical two vectors. They have to agree: that function's output is written
    // into the canonical payload, which is hashed and immutable, so a value the client accepts and
    // the server later reads back as null makes the booking permanently unfindable by phone with
    // no repair possible (Inv-2/3). If either side moves its threshold, one of these fails.

    [Fact]
    public void NormaliseAcceptsAValueAtExactlyTheRawCeiling()
    {
        var atLimit = "+" + new string('4', 63); // 64 raw chars
        Assert.Equal(64, atLimit.Length);
        Assert.Equal(atLimit, PhoneMatching.Normalise(atLimit));
    }

    [Fact]
    public void NormaliseRejectsAValueOverTheRawCeilingRatherThanTruncatingIt()
    {
        var overLimit = "+" + new string('4', 64); // 65 raw chars
        Assert.Equal(65, overLimit.Length);
        // Null, NOT a shortened number — a truncated phone is a different phone, and silently
        // dialling it is worse than refusing the value.
        Assert.Null(PhoneMatching.Normalise(overLimit));
    }

    [Fact]
    public void TheCeilingIsMeasuredOnRawLengthNotDigitCount()
    {
        // 40 digits, but padded with separators past the ceiling: the server bails BEFORE stripping
        // punctuation, so this is refused even though its digit count is unremarkable. The client
        // checks in the same order for the same reason.
        var padded = "+" + string.Join(" ", Enumerable.Repeat("44", 40));
        Assert.True(padded.Length > 64);
        Assert.Null(PhoneMatching.Normalise(padded));
    }
}
