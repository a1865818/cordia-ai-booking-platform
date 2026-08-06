namespace Cordia.Domain.Capacity;

/// <summary>
/// Pure helper that derives a deterministic first-party slot key from canonical payload fields
/// (spec 07 open-decision §4 / plan §2.4).
/// <para>
/// The key has the form <c>"{resource}|{startsAt:O}"</c> where <c>resource</c> is the first
/// non-null, non-empty value of <paramref name="resourceId"/>, <paramref name="serviceId"/>,
/// or <paramref name="locationId"/> (preference order). Returns <see langword="null"/> when all
/// three are null or empty — indicating the operation is not first-party-tracked.
/// </para>
/// </summary>
public static class CapacitySlotKey
{
    /// <summary>
    /// Derives the canonical slot key from the canonical payload's resource dimensions and
    /// the requested start time.
    /// </summary>
    /// <param name="resourceId">resource_id from canonical payload (preferred dimension).</param>
    /// <param name="serviceId">service_id from canonical payload (second choice).</param>
    /// <param name="locationId">location_id from canonical payload (fallback).</param>
    /// <param name="startsAt">starts_at (or requested_at fallback) from canonical payload, as a <see cref="DateTimeOffset"/>.</param>
    /// <returns>
    /// A deterministic slot key string, or <see langword="null"/> if no resource dimension is present.
    /// </returns>
    public static string? Derive(
        string? resourceId,
        string? serviceId,
        string? locationId,
        DateTimeOffset startsAt)
    {
        var resource = ResolveResource(resourceId, serviceId, locationId);
        if (resource is null)
            return null;

        // NORMALISED TO UTC before formatting. The "O" round-trip format prints the offset the
        // caller happened to carry, so the same instant expressed two ways produces two different
        // keys: the console declares slots with `toISOString()` (always `+00:00`) while a payload
        // from the agent lane can carry the venue's local offset. The declared slot and the
        // booking that should match it then key differently, `GetAsync` misses, the operation
        // takes the untracked path, and the venue's declared capacity is silently unenforced with
        // no error anywhere.
        //
        // Backward compatible: a value already in UTC formats identically either way, so slots
        // declared through the console keep the keys they were stored under.
        return $"{resource}|{startsAt.ToUniversalTime():O}";
    }

    /// <summary>
    /// The resource dimension the key is built from, in preference order, or <see langword="null"/>
    /// when none is present. Exposed so a caller that needs to STORE the dimension alongside the
    /// key resolves it the same way the key does — picking it independently (e.g. with a plain
    /// <c>??</c> chain, which treats <c>"  "</c> as a value) is how the stored dimension and the
    /// key it is supposed to describe drift apart.
    /// </summary>
    public static string? ResolveResource(string? resourceId, string? serviceId, string? locationId)
        => FirstNonEmpty(resourceId, serviceId, locationId);

    /// <summary>
    /// Whitespace-only is treated as absent, and the winner is trimmed.
    ///
    /// <para>This has to agree with <c>CreateCapacitySlotValidator</c>, which admits a slot when
    /// any dimension is non-WHITESPACE. Using <c>IsNullOrEmpty</c> here let <c>"  "</c> through as
    /// a real dimension, so a declared slot got the key <c>"  |{startsAt}"</c> while the booking
    /// path — deriving from a canonical payload that carries the meaningful dimension — derived a
    /// different key. The slot was stored and reported as declared capacity, and never matched or
    /// enforced by anything: precisely the silent no-op this type exists to prevent.</para>
    /// </summary>
    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate.Trim();
        }

        return null;
    }
}
