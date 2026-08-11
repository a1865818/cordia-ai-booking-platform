using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>
/// Guest contact details held OUT of the canonical payload (spec 12/14 retention ruling,
/// owner decision 2026-08-06).
///
/// <para><b>Why this table exists.</b> Contact written into
/// <c>booking_intent_revisions.canonical_payload</c> can never be erased: the payload is hashed,
/// <c>payload_hash</c> binds the confirmation to it, and the whole idempotency chain (Inv-2/3)
/// descends from that hash — redacting it makes the payload stop re-hashing to its own recorded
/// hash. Holding contact here and putting only an opaque <c>contact_ref</c> in the payload keeps
/// the hash valid and turns erasure into a row delete.</para>
///
/// <para>This is the option that was rejected at the original guest-identity design gate for
/// needing a new table. That choice is precisely what created the permanent-PII problem it now
/// resolves.</para>
///
/// <para>Not an entity the agent or console reads back: the ref is opaque to them. Only the
/// notification resolver and the discovery lookup dereference it, both server-side.</para>
/// </summary>
public class GuestContact : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Opaque handle carried on the canonical payload. Never derived from the contact.</summary>
    public string ContactRef { get; set; } = string.Empty;

    /// <summary>
    /// Normalised E.164-ish phone, formatting stripped. Nullable: a guest may leave only an email.
    /// </summary>
    public string? PhoneE164 { get; set; }

    public string? Email { get; set; }

    /// <summary>
    /// A name is not a notification destination, but it is contact data for retention purposes,
    /// so it lives here rather than in the immutable payload for the same reason the rest does.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Stable fingerprint of the normalised contact, used to make creation idempotent so a repeat
    /// booking for the same guest reuses one row instead of accumulating a row per booking.
    /// A hash rather than the raw value: this column is indexed and would otherwise be a second,
    /// equally un-erasable copy of the phone number in an index.
    /// </summary>
    public string ContactFingerprint { get; set; } = string.Empty;

    /// <summary>
    /// Fingerprint of tenant + normalised phone ALONE, independent of whether the guest also gave
    /// an email. Null when they left no phone.
    ///
    /// <para>Discovery matches on this rather than on <see cref="ContactFingerprint"/>, which folds
    /// in the email and therefore misses the same guest the moment they supply one, and rather than
    /// on <see cref="PhoneE164"/>, which would put the raw number into every lookup's query text.
    /// It is the column that lets phone discovery be an indexed exact match with no raw number in
    /// the predicate.</para>
    /// </summary>
    public string? PhoneFingerprint { get; set; }

    /// <summary>
    /// The same idea as <see cref="PhoneFingerprint"/> for the email channel: tenant + normalised
    /// email alone, so <c>GET /api/bookings?email=</c> can find a booking written in the
    /// out-of-band shape. Without it, a guest who left only an email is discoverable by no hint at
    /// all once their address stops being written into the payload. Null when they left no email.
    /// </summary>
    public string? EmailFingerprint { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Retention clock — refreshed every time this contact is reused for a new booking.
    ///
    /// <para><b>Why not <see cref="CreatedAt"/>.</b> Creation is idempotent per guest, so a guest
    /// who first booked two years ago keeps that original <c>CreatedAt</c> for the booking they
    /// make today. Sweeping on it erases the contact out from under a booking that has not even
    /// happened yet: the <c>contact_ref</c> stops dereferencing, the confirmation settles with no
    /// destination, and the guest is never told. Measuring from last use means the window is
    /// "90 days since we last had a reason to hold this", which is what retention actually means.</para>
    /// </summary>
    public DateTimeOffset LastUsedAt { get; set; }
}
