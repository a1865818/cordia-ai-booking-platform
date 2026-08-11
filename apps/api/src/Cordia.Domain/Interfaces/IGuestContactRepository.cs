using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Store for guest contact held outside the immutable canonical payload
/// (spec 12/14 retention ruling, owner decision 2026-08-06).
/// </summary>
public interface IGuestContactRepository
{
    /// <summary>
    /// Returns the existing ref for this contact within the tenant, or creates one.
    /// Idempotent on the normalised contact so repeat bookings for one guest share a row.
    /// </summary>
    Task<GuestContact> GetOrCreateAsync(
        string tenantId,
        string? phoneE164,
        string? email,
        string? displayName,
        CancellationToken ct = default);

    /// <summary>Dereferences a ref. <c>null</c> when unknown OR already erased by retention.</summary>
    Task<GuestContact?> GetByRefAsync(string tenantId, string contactRef, CancellationToken ct = default);

    /// <summary>
    /// Refs whose phone matches, for discovery. Returns refs, never contact values — the caller
    /// is matching bookings, not reading a directory.
    /// </summary>
    Task<IReadOnlyList<string>> FindRefsByPhoneAsync(
        string tenantId, string phoneE164, CancellationToken ct = default);

    /// <summary>
    /// The email equivalent of <see cref="FindRefsByPhoneAsync"/>. Without it a guest who left only
    /// an email is findable by no hint at all once contact moves out of the payload, because the
    /// payload then carries neither their address nor anything derived from it.
    /// </summary>
    Task<IReadOnlyList<string>> FindRefsByEmailAsync(
        string tenantId, string email, CancellationToken ct = default);

    /// <summary>
    /// Raises a contact's retention clock to at least <paramref name="usedAt"/>, never lowering it.
    ///
    /// <para>Exists because "last used" measured at CAPTURE is not the same as "last needed".
    /// Nothing touches the row between the moment a booking is made and the moment it happens, so
    /// a booking taken further ahead than the retention window had its contact erased before the
    /// guest ever arrived: the confirmation and every reminder then settled with no destination
    /// and nobody was told. Finalize pushes the clock out to the booking's own start instant, so
    /// the window means "90 days after we last had a reason to hold this" for a future booking as
    /// well as a past one.</para>
    ///
    /// <para>Monotonic on purpose: a later booking must never shorten the window an earlier one
    /// established. No-ops when the ref is unknown or already erased.</para>
    /// </summary>
    Task TouchAsync(string tenantId, string contactRef, DateTimeOffset usedAt, CancellationToken ct = default);

    /// <summary>
    /// Erases contact rows LAST USED before <paramref name="cutoff"/>. Returns rows deleted —
    /// this is the erasure the old inline shape made impossible.
    ///
    /// <para>Last used, not created: <see cref="GetOrCreateAsync"/> is idempotent, so a returning
    /// guest keeps their original creation date forever and erasing on it would take the contact
    /// away from a booking that has not happened yet. Every call to <see cref="GetOrCreateAsync"/>
    /// re-arms the window, so a contact survives exactly as long as it keeps being used.</para>
    /// </summary>
    Task<int> EraseOlderThanAsync(string tenantId, DateTimeOffset cutoff, CancellationToken ct = default);
}
