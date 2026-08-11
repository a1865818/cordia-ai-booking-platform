namespace Cordia.Domain.Bookings;

/// <summary>
/// Masks a <c>customer_ref</c> that turns out to be a REAL contact address, for the console list
/// reads that are tenant-scoped but otherwise ungated (spec 14 minimisation, Inv-12).
///
/// <para><b>Why this is needed at all.</b> `customer_ref` was documented as opaque and is not.
/// `bookings.customer_id` is resolved by <c>FinalizeBookingCommandHandler</c> through
/// <c>customer_id → contact_ref → caller_id → phone_e164 → email</c>, and
/// <c>waitlist_enrollments.customer_ref</c> is the value
/// <c>NotificationRecipientResolver.AsDeliverable</c> hands to the SMS/email adapter as the send
/// destination. The console mints an opaque <c>contact_ref</c> before hashing a payload, so
/// console-created bookings were already minimised — but the agent lane never mints one and carries
/// <c>phone_e164</c> inline, so agent-created bookings landed with a raw phone number that
/// <c>GET /api/customers</c>, <c>GET /api/Bookings/schedule</c>,
/// <c>GET /api/conversation-sessions</c> and <c>GET /api/waitlist/enrollments</c> then returned to
/// any caller holding tenant scope — no grant, no reason code, no audit row. That is a strictly
/// lower bar than the transcript read of the same data class.</para>
///
/// <para><b>What it deliberately does NOT do.</b> It is not a hash and not an encoding: it is
/// lossy on purpose, so nothing downstream can be tempted to treat the output as an address. It
/// passes through anything that is not a deliverable address unchanged — an opaque
/// <c>gc_*</c> handle, a provider reference, an internal id — because those are what the projection
/// is supposed to carry and re-masking them would only make the console less useful.</para>
///
/// <para><b>Grouping still uses the RAW value.</b> Callers mask at the point of emission, after any
/// grouping or counting, so per-guest aggregates stay exact. Two different guests can mask to the
/// same label; that is a display collision, not a counting error.</para>
///
/// <para>The un-masked value remains available where it is actually gated:
/// <c>GET /api/Bookings/{bookingId}</c> behind a spec-08 grant.</para>
/// </summary>
public static class ContactMinimisation
{
    private const char Dot = '•';
    private const string Dots = "•••";

    /// <summary>How many trailing digits of a phone survive, enough to recognise a known guest.</summary>
    private const int PhoneTailDigits = 3;

    /// <summary>
    /// The value safe to return on an ungated list read: masked when it is a phone or an email,
    /// unchanged otherwise.
    /// </summary>
    public static string? Mask(string? customerRef)
    {
        if (string.IsNullOrWhiteSpace(customerRef))
            return customerRef;

        var trimmed = customerRef.Trim();

        // Email first: it is the unambiguous shape, and `PhoneMatching.Normalise` would otherwise
        // strip an address down to whatever digits it happens to contain.
        var at = trimmed.IndexOf('@');
        if (at > 0 && at == trimmed.LastIndexOf('@') && at < trimmed.Length - 1
            && trimmed.AsSpan(at + 1).Contains('.'))
        {
            // First character plus the domain: enough for an operator to recognise an address they
            // already know, not enough to learn one they do not.
            return $"{trimmed[0]}{Dots}{trimmed[at..]}";
        }

        // A SHAPE check before the normaliser, and it is load-bearing.
        //
        // `PhoneMatching.Normalise` exists to match numbers a human typed, so it discards every
        // non-digit — which means it happily "normalises" `gc_9f2c41ab8e004d10` to `9241800410`
        // and `op_7c1d` to `71`. Masking on that alone mangled every opaque handle the projection
        // is actually supposed to carry, turning the console's guest labels into `•••410`. A phone
        // may contain digits and separator punctuation and nothing else; a letter or an underscore
        // means this is a handle, not a number.
        foreach (var c in trimmed)
        {
            if (char.IsLetter(c) || c == '_')
                return customerRef;
        }

        var normalised = PhoneMatching.Normalise(trimmed);
        if (normalised is null)
            return customerRef;

        var digits = normalised.TrimStart('+');
        return digits.Length <= PhoneTailDigits
            ? Dots
            : $"{Dots}{digits[^PhoneTailDigits..]}";
    }

    /// <summary>
    /// Whether <paramref name="customerRef"/> is a real contact address rather than an opaque
    /// handle — i.e. whether <see cref="Mask"/> would change it.
    /// </summary>
    public static bool IsContactAddress(string? customerRef)
        => !string.IsNullOrWhiteSpace(customerRef)
            && !string.Equals(Mask(customerRef), customerRef, StringComparison.Ordinal);
}
