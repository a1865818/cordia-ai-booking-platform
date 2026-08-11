using System.Net.Mail;

namespace Cordia.Domain.Bookings;

/// <summary>
/// Whether a supplied contact string is a DESTINATION the platform could actually reach.
///
/// <para>The counterpart to <see cref="PhoneMatching.Normalise"/> for the email channel, and it
/// exists for the same reason: a handle that dereferences to something undeliverable is worse than
/// no handle, because the booking then carries a reference implying a destination exists when none
/// does (Inv-16). A blank check is not that test — <c>"."</c> and <c>"a"</c> are both non-blank and
/// neither can be sent to.</para>
/// </summary>
public static class ContactAddress
{
    /// <summary>
    /// Guards against a pathological input being handed to the parser, and against storing a
    /// value no provider would accept. RFC 5321 caps a path at 256 octets; this is deliberately
    /// close to that rather than generous.
    /// </summary>
    private const int MaxEmailLength = 254;

    /// <summary>
    /// The trimmed address when it parses as a single deliverable mailbox, otherwise
    /// <see langword="null"/>.
    ///
    /// <para>Deliberately a shape check, not a proof of delivery — nothing short of sending can
    /// establish that, and this runs on the booking path. It rejects what is definitely not an
    /// address so the caller gets a loud 400 instead of a ref that silently notifies nobody.</para>
    /// </summary>
    public static string? NormaliseEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        var trimmed = email.Trim();

        if (trimmed.Length > MaxEmailLength)
            return null;

        // MailAddress accepts a display-name form ("Guest <a@b.com>") and comma-separated lists,
        // neither of which is a bare address the notification layer can store and use. Requiring
        // the parsed Address to equal the input rejects both without hand-rolling a grammar.
        if (!MailAddress.TryCreate(trimmed, out var parsed)
            || !string.Equals(parsed.Address, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // A domain with no dot is not routable on the public internet. `MailAddress` accepts
        // `a@localhost`, which is valid syntax and not a destination a venue's guest ever has.
        var at = trimmed.LastIndexOf('@');
        var domain = trimmed[(at + 1)..];

        return domain.Contains('.', StringComparison.Ordinal)
               && !domain.StartsWith('.')
               && !domain.EndsWith('.')
            ? trimmed
            : null;
    }
}
