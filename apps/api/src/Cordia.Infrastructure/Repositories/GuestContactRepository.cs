using System.Security.Cryptography;
using System.Text;
using Cordia.Domain.Bookings;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>
/// Guest contact held outside the immutable canonical payload
/// (spec 12/14 retention ruling, owner decision 2026-08-06).
/// </summary>
public class GuestContactRepository(ApplicationDbContext db) : IGuestContactRepository
{
    public async Task<GuestContact> GetOrCreateAsync(
        string tenantId,
        string? phoneE164,
        string? email,
        string? displayName,
        CancellationToken ct = default)
    {
        var phone = PhoneMatching.Normalise(phoneE164);

        // Held to the same standard as the phone: a value that is not a DESTINATION is not stored
        // as one. Lower-cased after validation so the fingerprint is stable for a guest who
        // capitalises differently between bookings.
        //
        // A blank check alone let `"."` or `"guest@localhost"` through, so the row could hold an
        // address nothing could deliver to while the mint audit — which reports the normalised
        // value — recorded no email at all. The row and the one immutable record of writing it
        // disagreed, and `NotificationRecipientResolver` does no deliverability re-check of its
        // own, so it would have trusted whichever it read.
        var mail = ContactAddress.NormaliseEmail(email)?.ToLowerInvariant();
        var name = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        var fingerprint = Fingerprint(tenantId, phone, mail);
        var now = DateTimeOffset.UtcNow;

        var existing = await db.GuestContacts
            .FirstOrDefaultAsync(g => g.TenantId == tenantId && g.ContactFingerprint == fingerprint, ct);
        if (existing is not null)
        {
            // Touch the retention clock — MONOTONICALLY. This call means a booking is being made
            // against this contact right now, so the row is needed for at least another full
            // window; without it, a returning guest is erased on the age of their first-ever
            // booking and the booking they just made loses its only notification destination.
            //
            // Never assigned unconditionally: `TouchAsync` pushes the clock out to a FUTURE
            // booking's start instant, and a plain `= now` would drag it back. A guest with a
            // booking six months out who books again today would have had their retention window
            // silently shortened to today — erasing the contact months before the booking it was
            // being held for.
            await TouchAsync(tenantId, existing.ContactRef, now, ct);
            return existing;
        }

        var row = new GuestContact
        {
            TenantId = tenantId,
            ContactRef = $"gc_{Guid.NewGuid():N}",
            PhoneE164 = phone,
            Email = mail,
            DisplayName = name,
            ContactFingerprint = fingerprint,
            PhoneFingerprint = ChannelFingerprint(tenantId, phone),
            EmailFingerprint = ChannelFingerprint(tenantId, mail),
            CreatedAt = now,
            LastUsedAt = now,
        };

        // ON CONFLICT DO NOTHING, not catch-the-violation.
        //
        // The pre-read above is a fast path, not a lock: two bookings for the same guest — a
        // double-click, or two agent-lane retries — both miss it under READ COMMITTED and both
        // insert against `ix_guest_contacts_tenant_id_contact_fingerprint`.
        //
        // Catching the violation cannot work, and the reason is easy to miss: this now runs inside
        // the caller's transaction, and PostgreSQL aborts the WHOLE transaction on a constraint
        // violation. By the time a catch block ran, the transaction would already be poisoned, so
        // the "read the winner back" query — and the caller's COMMIT — would fail with
        // `current transaction is aborted`, turning an idempotent 200 into a 500. Letting the
        // database absorb the conflict raises no exception, so the transaction stays clean.
        // `CapacitySlotRepository.TryAddAsync` and `IdempotencyRepository.ElectCreatorAsync` use
        // the same primitive for the same reason.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO public.guest_contacts
                (tenant_id, contact_ref, phone_e164, email, display_name,
                 contact_fingerprint, phone_fingerprint, email_fingerprint, created_at, last_used_at)
            VALUES
                ({row.TenantId}, {row.ContactRef}, {row.PhoneE164}, {row.Email}, {row.DisplayName},
                 {row.ContactFingerprint}, {row.PhoneFingerprint}, {row.EmailFingerprint},
                 {row.CreatedAt}, {row.LastUsedAt})
            ON CONFLICT (tenant_id, contact_fingerprint) DO NOTHING
            """,
            ct);

        if (inserted == 1)
            return row;

        // Lost the race. The winner's row is the right answer — a second ref for one guest would
        // mean erasing them took two deletes, and the second would be missed.
        var winner = await db.GuestContacts
            .FirstOrDefaultAsync(g => g.TenantId == tenantId && g.ContactFingerprint == fingerprint, ct)
            ?? throw new InvalidOperationException(
                $"Guest contact insert for tenant '{tenantId}' conflicted but the winning row could not be read.");

        // Same reasoning as the hit above, and monotonic for the same reason.
        await TouchAsync(tenantId, winner.ContactRef, now, ct);
        return winner;
    }

    public Task<GuestContact?> GetByRefAsync(string tenantId, string contactRef, CancellationToken ct = default)
        => db.GuestContacts
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.TenantId == tenantId && g.ContactRef == contactRef, ct);

    public async Task<IReadOnlyList<string>> FindRefsByPhoneAsync(
        string tenantId, string phoneE164, CancellationToken ct = default)
    {
        var phone = PhoneMatching.Normalise(phoneE164);
        if (string.IsNullOrWhiteSpace(phone))
            return [];

        // Matched on the PHONE fingerprint, so the query never sends the raw number to the database
        // in a form a slow-query log would capture.
        //
        // Deliberately not ContactFingerprint: that one folds in the email, so it stops matching
        // the moment the same guest supplies one — a phone lookup would silently miss exactly the
        // guests the platform knows most about. Deliberately not `PhoneE164 == phone` either: it
        // matches correctly but parameterises the raw number, which is the exposure this column
        // exists to avoid, and it has no index behind it.
        var phoneFingerprint = ChannelFingerprint(tenantId, phone);

        return await db.GuestContacts
            .AsNoTracking()
            .Where(g => g.TenantId == tenantId && g.PhoneFingerprint == phoneFingerprint)
            .Select(g => g.ContactRef)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> FindRefsByEmailAsync(
        string tenantId, string email, CancellationToken ct = default)
    {
        // Normalised exactly as GetOrCreateAsync normalises it, or the fingerprints would not meet.
        var mail = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
        if (mail is null)
            return [];

        var emailFingerprint = ChannelFingerprint(tenantId, mail);

        return await db.GuestContacts
            .AsNoTracking()
            .Where(g => g.TenantId == tenantId && g.EmailFingerprint == emailFingerprint)
            .Select(g => g.ContactRef)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task TouchAsync(
        string tenantId, string contactRef, DateTimeOffset usedAt, CancellationToken ct = default)
        // Conditional on the stored value, so this can only ever push the clock FORWARD: a
        // booking made today must not pull the window in on a contact already held for a booking
        // six months out. `ExecuteUpdate` keeps it to one statement with no read-modify-write, so
        // two concurrent bookings for the same guest cannot lose one another's extension.
        => await db.GuestContacts
            .Where(g => g.TenantId == tenantId && g.ContactRef == contactRef && g.LastUsedAt < usedAt)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastUsedAt, usedAt), ct);

    public async Task<int> EraseOlderThanAsync(
        string tenantId, DateTimeOffset cutoff, CancellationToken ct = default)
        // This is the whole point of the table: a real DELETE. The canonical payload keeps its
        // `contact_ref`, so every hash still verifies — the ref simply stops dereferencing, which
        // is exactly what erasure should look like from the outside.
        //
        // Keyed on LAST USE, not creation. Creation is idempotent per guest, so `CreatedAt` is the
        // date the guest was first ever seen and stops moving; sweeping on it erases the contact
        // for a booking made yesterday because the guest also booked two years ago.
        //
        // `LastUsedAt` is advanced by GetOrCreateAsync at capture AND pushed out to the booking's
        // own start instant by TouchAsync at finalize. Capture alone was not enough: nothing
        // touches the row between making a booking and honouring it, so a booking taken further
        // ahead than the window still lost its contact before the guest arrived. With both, the
        // window means "90 days after we last had a reason to hold this" — where the reason
        // includes a booking that has not happened yet.
        //
        // IgnoreQueryFilters + an EXPLICIT tenant predicate, matching RetentionSweeper's own
        // reads: the only caller is the cross-tenant sweep, which runs in a background scope with
        // no ambient tenant. Leaning on the global filter there does not merely return nothing —
        // it threw, and the sweeper's per-tenant catch swallowed it, so retention for that tenant
        // silently stopped. The predicate below is the tenant scope (Inv-13); it is not optional
        // and must never be relaxed to a bare cutoff.
        => await db.GuestContacts
            .IgnoreQueryFilters()
            .Where(g => g.TenantId == tenantId && g.LastUsedAt < cutoff)
            .ExecuteDeleteAsync(ct);

    /// <summary>
    /// Identity fingerprint: tenant + phone + email. Tenant-salted so the same phone in two
    /// tenants produces different fingerprints — otherwise the unique index would let one tenant
    /// confirm another's guest exists by probing.
    /// </summary>
    private static string Fingerprint(string tenantId, string? phone, string? email)
        => Hash(tenantId, phone, email);

    /// <summary>
    /// Discovery fingerprint for ONE channel: tenant + that channel's normalised value alone, so
    /// it still matches a guest whose identity fingerprint also folded in the other channel.
    /// Null in, null out — a guest who left no phone is not discoverable by one, and likewise for
    /// email.
    /// </summary>
    private static string? ChannelFingerprint(string tenantId, string? value)
        => string.IsNullOrWhiteSpace(value) ? null : Hash(tenantId, value);

    /// <summary>
    /// ASCII unit separator (0x1F). Cannot occur in a normalised phone, an email address, or a
    /// tenant id, so it splits the hashed fields unambiguously.
    /// </summary>
    private const char FieldSeparator = (char)0x1F;

    /// <summary>
    /// Fields are SEPARATED before hashing. Bare concatenation lets genuinely different contacts
    /// hash the same: phone <c>+61</c> with email <c>400@x.com</c>, and phone <c>+61400</c> with
    /// email <c>@x.com</c>, both flatten to one identical string. The unique index would then hand
    /// the first guest's ref to the second — notifying the wrong person, and making either guest's
    /// erasure erase both.
    /// </summary>
    private static string Hash(params string?[] fields)
    {
        var material = string.Join(FieldSeparator, fields.Select(f => f ?? string.Empty));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}
