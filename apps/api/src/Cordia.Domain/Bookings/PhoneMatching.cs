namespace Cordia.Domain.Bookings;

/// <summary>
/// Formatting-only normalisation for comparing two phone numbers.
///
/// <para><b>Why this exists.</b> A guest phone is written into the canonical payload, which is
/// hashed (canon.v1) and immutable — so a stored number can never be rewritten into a canonical
/// form after the fact without invalidating <c>payload_hash</c> and every idempotency record
/// derived from it (Inv-2/3). Normalisation therefore has to happen either at capture, before
/// hashing, or at comparison time. This is the comparison-time half: it lets a booking stored as
/// <c>"+61 400 111 222"</c> be found by a search for <c>"+61400111222"</c>.</para>
///
/// <para><b>What it deliberately does NOT do.</b> It does not convert a national trunk prefix to
/// a country code — <c>0400111222</c> does not become <c>+61400111222</c>. That conversion needs
/// to know the tenant's country, which the platform does not record anywhere, and guessing would
/// silently match one guest's booking to another's number in any country whose national format
/// collides. Formatting is objective; country is policy. Only the objective half is done here,
/// and the console warns at capture when a number is not already E.164.</para>
/// </summary>
public static class PhoneMatching
{
    /// <summary>
    /// Longest input treated as a possible phone number. E.164 caps at 15 digits; this leaves
    /// generous room for separators while keeping the buffer bounded.
    /// </summary>
    private const int MaxLength = 64;

    /// <summary>
    /// Strips separators a human or a form might introduce — spaces, hyphens, parentheses, dots
    /// and non-breaking spaces — keeping digits and a single leading <c>+</c>.
    ///
    /// Returns <c>null</c> for a value with no digits at all, so an empty or punctuation-only
    /// string can never compare equal to another one.
    /// </summary>
    public static string? Normalise(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();

        // A '+' counts as the international prefix when it appears BEFORE any digit — so
        // "(+61) 400 111 222" keeps its plus, which StartsWith('+') would have missed because
        // the parenthesis comes first. A '+' after the digits begin is separator noise.
        var hasPlus = false;
        foreach (var c in trimmed)
        {
            if (char.IsAsciiDigit(c)) break;
            if (c == '+') { hasPlus = true; break; }
        }

        // A phone number is never this long. Bailing beats stackallocing a caller-controlled
        // length: the value reaches here from a query parameter, a canonical payload and an
        // unbounded DB text column, and a stack overflow in .NET cannot be caught — it takes the
        // whole process down.
        if (trimmed.Length > MaxLength)
            return null;

        Span<char> buffer = stackalloc char[trimmed.Length + 1];
        var length = 0;
        if (hasPlus)
            buffer[length++] = '+';

        foreach (var c in trimmed)
        {
            if (char.IsAsciiDigit(c))
                buffer[length++] = c;
        }

        // "+" alone, or a string of pure punctuation, is not a number.
        var digitCount = hasPlus ? length - 1 : length;
        return digitCount == 0 ? null : new string(buffer[..length]);
    }

    /// <summary>
    /// True when two numbers are the same once formatting is removed. Two values that both
    /// normalise to <c>null</c> are NOT equal — absent does not match absent.
    /// </summary>
    public static bool Matches(string? a, string? b)
    {
        var left = Normalise(a);
        var right = Normalise(b);
        return left is not null && right is not null
            && string.Equals(left, right, StringComparison.Ordinal);
    }
}
