using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cordia.Domain.Bookings;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Notifications;

namespace Cordia.Infrastructure.Providers;

/// <summary>
/// Resolves the delivery destination for a booking operation from the guest carried on its
/// confirmed intent revision (spec 04 §4.2 `customer_ref` / `notification_destination`).
///
/// <para>Before this existed the resolver was a stub returning <c>SHA256(tenant|operation)</c>,
/// so EVERY booking — agent-made and console-made alike — had a syntactically valid but entirely
/// fictional recipient, and the <c>no_destination</c> branch in
/// <c>SendNotificationCommandHandler</c> was unreachable.</para>
///
/// <para><b>Two values, deliberately.</b> <see cref="ResolvedRecipient.RecipientRef"/> is a
/// minimized token that is persisted on the notification row and keys dedup;
/// <see cref="ResolvedRecipient.Destination"/> is the real address and is never persisted or
/// logged.</para>
///
/// <para><b>The token stays derived from the operation id, NOT from the destination.</b> It feeds
/// <c>NotificationIdentity.Derive(...)</c>, so changing how it is computed would change every
/// <c>notification_id</c> and a message redelivered across that boundary would no longer dedup —
/// the guest would be notified twice (Inv-9). The operation id is already part of that identity,
/// so a destination-derived token would buy nothing and risk exactly that.</para>
///
/// <para><b>The guest is read from the <c>customer</c> node only.</b> Not via the tolerant
/// whole-payload search <c>CanonicalBookingPayload</c> does for the read projection: that recurses
/// into every nested object and stops at the first key match, and its key list includes a bare
/// <c>"phone"</c> alongside <c>business</c>/<c>venue_name</c> support. Tolerable when it feeds a
/// display projection; not when it decides where a message is delivered — a payload carrying a
/// venue phone could otherwise capture the send.</para>
///
/// <para><b>No guest ⇒ no destination.</b> The operation-derived handle is still returned so the
/// notification row has a stable ref, but <c>Destination</c> is null and the caller must treat that
/// as "nowhere to deliver" rather than sending into the void.</para>
/// </summary>
public sealed class NotificationRecipientResolver(
    IBookingOperationRepository operations,
    IBookingIntentRepository intents,
    IWaitlistOfferRepository offers,
    IWaitlistEnrollmentRepository enrollments,
    IGuestContactRepository guestContacts) : INotificationRecipientResolver
{
    /// <inheritdoc/>
    public async Task<ResolvedRecipient?> ResolveAsync(
        string tenantId,
        string operationId,
        CancellationToken ct = default)
    {
        var guest = await TryResolveGuestAsync(tenantId, operationId, ct);

        // The ref is operation-derived in BOTH branches — see the class remarks: it keys
        // notification_id, so it must not move when a guest is present.
        var recipientRef = Token(tenantId, operationId);

        return guest is null
            ? new ResolvedRecipient(recipientRef, NotificationChannel.Sms, "en", Destination: null)
            : new ResolvedRecipient(recipientRef, guest.Value.Channel, "en", guest.Value.Destination);
    }

    private async Task<(string Destination, NotificationChannel Channel)?> TryResolveGuestAsync(
        string tenantId,
        string operationId,
        CancellationToken ct)
        => await TryResolveFromBookingAsync(tenantId, operationId, ct)
           ?? await TryResolveFromWaitlistAsync(tenantId, operationId, ct);

    /// <summary>
    /// The waitlist lane. A waitlist offer/expiry notification carries an <c>op_wl_*</c> id that
    /// has NO <c>booking_operations</c> row until the offer is accepted, so the booking lookup
    /// above always misses for it — which is why an earlier attempt at the no-destination guard
    /// terminal-failed a working lane. The address lives on the enrollment instead.
    /// </summary>
    private async Task<(string Destination, NotificationChannel Channel)?> TryResolveFromWaitlistAsync(
        string tenantId,
        string operationId,
        CancellationToken ct)
    {
        var offer = await offers.GetByOperationIdAsync(operationId, ct);
        if (offer is null || !string.Equals(offer.TenantId, tenantId, StringComparison.Ordinal))
            return null;

        // The offer is only the route to the enrollment, which is where the address lives.
        return await TryResolveFromEnrollmentAsync(tenantId, offer.EnrollmentId, ct);
    }

    /// <inheritdoc/>
    public async Task<ResolvedRecipient?> ResolveByEnrollmentAsync(
        string tenantId,
        string enrollmentId,
        CancellationToken ct = default)
    {
        var guest = await TryResolveFromEnrollmentAsync(tenantId, enrollmentId, ct);
        if (guest is null)
            return null;

        // Enrollment-derived, and deliberately so: this path has no operation id to derive from.
        // It is NOT the row's persisted ref — see the interface remarks. Reconcile/retry keep using
        // the stored one, which is what keys notification_id (Inv-9).
        return new ResolvedRecipient(
            Token(tenantId, enrollmentId), guest.Value.Channel, "en", guest.Value.Destination);
    }

    /// <summary>
    /// The enrollment tail shared by the offer lookup and the enrollment-keyed recovery entry
    /// point. One implementation on purpose: the send path and the reconcile/retry paths must not
    /// be able to disagree about where a waitlist guest is reachable.
    /// </summary>
    private async Task<(string Destination, NotificationChannel Channel)?> TryResolveFromEnrollmentAsync(
        string tenantId,
        string enrollmentId,
        CancellationToken ct)
    {
        var enrollment = await enrollments.GetAsync(enrollmentId, ct);
        if (enrollment is null || !string.Equals(enrollment.TenantId, tenantId, StringComparison.Ordinal))
            return null;

        // CustomerRef is captured as "Name or contact handle" — it may be a person's NAME, which
        // is not somewhere a message can be sent. Treating it as a destination unconditionally
        // would assert that "John Smith" is deliverable, so it is only used when it actually
        // looks like one. An enrollment identified only by name genuinely has nowhere to send.
        var destination = AsDeliverable(enrollment.CustomerRef);
        if (destination is null)
            return null;

        return (destination.Value.Destination,
                ChannelFor(enrollment.NotificationChannel, destination.Value.Channel));
    }

    /// <summary>
    /// Classifies a free-text reference as a deliverable address, or null when it is neither a
    /// phone nor an email — a name, a nickname, or an internal handle.
    /// </summary>
    internal static (string Destination, NotificationChannel Channel)? AsDeliverable(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();

        // An email is the unambiguous case: exactly one '@', with something either side and a
        // dot in the domain.
        var at = trimmed.IndexOf('@');
        if (at > 0 && at == trimmed.LastIndexOf('@') && at < trimmed.Length - 1
            && trimmed.AsSpan(at + 1).Contains('.'))
        {
            return (trimmed, NotificationChannel.Email);
        }

        // A phone must be in international form. A bare national number cannot be dialled
        // without a country, which the platform does not record (see PhoneMatching).
        var phone = PhoneMatching.Normalise(trimmed);
        if (phone is not null && phone.StartsWith('+') && phone.Length >= 8)
            return (phone, NotificationChannel.Sms);

        return null;
    }

    /// <summary>
    /// The channel the DESTINATION can actually be reached on, which is the only one worth
    /// attempting.
    ///
    /// <para>This used to prefer the enrollment's stated channel and fall back to the address
    /// shape. But the stated value is free text validated only as non-empty, while
    /// <c>AsDeliverable</c> has already classified the real destination — so when the two
    /// disagreed the resolver handed an email address to the SMS adapter, or a phone number to the
    /// email adapter. That send cannot succeed; it can only fail in a way that looks like a
    /// provider problem.</para>
    ///
    /// <para>The stated channel still wins where it is <em>compatible</em>: <c>channel</c> means
    /// "reply on the conversation channel" and carries no address shape of its own, so it is
    /// honoured as given. Where the operator's stated preference genuinely conflicts with the only
    /// address on file, the address wins — the console now refuses to record that combination in
    /// the first place, so a stored conflict is legacy data, not a new choice.</para>
    /// </summary>
    internal static NotificationChannel ChannelFor(string? stated, NotificationChannel implied)
        => stated?.Trim().ToLowerInvariant() switch
        {
            "channel" => NotificationChannel.Channel,
            "sms" when implied == NotificationChannel.Sms => NotificationChannel.Sms,
            "email" when implied == NotificationChannel.Email => NotificationChannel.Email,
            _ => implied,
        };

    private async Task<(string Destination, NotificationChannel Channel)?> TryResolveFromBookingAsync(
        string tenantId,
        string operationId,
        CancellationToken ct)
    {
        var operation = await operations.GetAsync(operationId, ct);

        // GetAsync leans on the ambient global query filter; re-check explicitly, the way
        // IBookingIntentRepository.IntentExistsAsync does, so a mis-scoped ambient context can
        // never route one tenant's notification to another tenant's guest (Inv-13).
        if (operation is null || !string.Equals(operation.TenantId, tenantId, StringComparison.Ordinal))
            return null;

        var revision = await intents.GetRevisionAsync(
            tenantId, operation.IntentId, operation.IntentRevision, ct);

        if (revision is null)
            return null;

        // Current shape first: the payload carries an opaque `contact_ref` and the destination
        // lives in `guest_contacts`, so it can be erased. An erased contact resolves to nothing
        // and the notification correctly settles `no_destination` rather than claiming a delivery
        // it cannot make (Inv-16).
        var contactRef = ReadContactRef(revision.CanonicalPayload);
        if (contactRef is not null)
        {
            var contact = await guestContacts.GetByRefAsync(tenantId, contactRef, ct);
            if (contact is null)
                return null;

            if (!string.IsNullOrWhiteSpace(contact.PhoneE164))
                return (contact.PhoneE164, NotificationChannel.Sms);
            if (!string.IsNullOrWhiteSpace(contact.Email))
                return (contact.Email, NotificationChannel.Email);
            return null;
        }

        // Legacy shape: revisions written before the retention ruling hold the contact inline and
        // are immutable, so they still have to be readable.
        return ReadGuestDestination(revision.CanonicalPayload);
    }

    /// <summary>
    /// Reads the delivery address from the payload's top-level <c>customer</c> object — and only
    /// from there. A malformed or absent node yields null rather than throwing: a historical row
    /// must never take down the notification lane.
    /// </summary>
    private static (string Destination, NotificationChannel Channel)? ReadGuestDestination(string json)
    {
        JsonElement customer;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("customer", out var node)
                || node.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // The document is disposed on leaving this scope, so take a detached copy.
            customer = node.Clone();
        }
        catch (JsonException)
        {
            return null;
        }

        // Phone only — and that is not an omission.
        //
        // `CustomerRef` (state.py) is `extra: forbid` and declares exactly `customer_id`,
        // `display_name`, `phone_e164`. There is NO email slot, so a `customer.email` branch
        // here would be unreachable code advertising support that does not exist: a guest who
        // asked to be emailed would silently get an SMS-channel notification with no address.
        // When spec 10's recipient-store decision lands and the contract gains an email field,
        // this is where the Email channel goes.
        var phone = ReadString(customer, "phone_e164");
        return phone is not null ? (phone, NotificationChannel.Sms) : null;
    }

    /// <summary>
    /// The opaque handle from the payload's <c>customer</c> node, when the payload uses the
    /// out-of-band shape. <c>null</c> means "this is a legacy inline payload", which is a
    /// different fact from "this guest has no contact".
    /// </summary>
    private static string? ReadContactRef(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("customer", out var node)
                || node.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return ReadString(node, "contact_ref");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>
    /// Minimized, deterministic handle. Same input ⇒ same token, so notification dedup stays
    /// stable across retries and reconciles; the token is one-way, so the persisted row and the
    /// logs never carry the contact value itself.
    /// </summary>
    private static string Token(string tenantId, string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}|{value}"));
        return "recip_" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
