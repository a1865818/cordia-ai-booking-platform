using System.Text.Json;

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

    /// <summary>
    /// The capacity request a confirmed intent revision's canonical payload describes: how many
    /// units it wants, and which first-party slot they are wanted against (<see langword="null"/>
    /// when the payload carries no resource/time dimension — i.e. not first-party-tracked).
    ///
    /// <para>Lives here, beside <see cref="Derive"/>, because more than one handler has to answer
    /// "which slot is this operation for?" from the same payload and they MUST agree. The reserve
    /// lane derives the slot it reserves against; the modify lane derives the slot it is moving TO,
    /// so it can tell a genuine reschedule (release the old seat) from a same-slot amendment (keep
    /// it). Two private copies of this parse drifting apart would put those two decisions on
    /// different keys, which is the failure this type already exists to prevent for declare-vs-reserve.</para>
    ///
    /// <para>Every field is optional and every failure is soft: an unparseable payload is
    /// "not tracked", never an exception. A read of a historical revision must not be able to fail
    /// an operation.</para>
    /// </summary>
    public static (int RequestedCapacity, string? SlotId) DeriveFromCanonicalPayload(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "{}")
            return (1, null);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // party_size — default 1 (a booking always needs >= 1 unit).
            // Guard ValueKind == Number FIRST: a real canonical payload serializes an absent
            // party_size as explicit JSON null, and JsonElement.TryGetInt32() THROWS
            // (InvalidOperationException, not JsonException) on a non-Number element.
            var requestedCapacity = 1;
            if (root.TryGetProperty("party_size", out var partySizeEl)
                && partySizeEl.ValueKind == JsonValueKind.Number
                && partySizeEl.TryGetInt32(out var ps)
                && ps > 0)
            {
                requestedCapacity = ps;
            }

            var resourceId = TryGetString(root, "resource_id");
            var serviceId = TryGetString(root, "service_id");
            var locationId = TryGetString(root, "location_id");

            // starts_at is the canonical agent field; requested_at is the back-compat fallback for
            // payloads written before the agent was updated.
            DateTimeOffset startsAt;
            if (root.TryGetProperty("starts_at", out var startsAtEl)
                && startsAtEl.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(startsAtEl.GetString(), out var parsedStartsAt))
            {
                startsAt = parsedStartsAt;
            }
            else if (root.TryGetProperty("requested_at", out var requestedAtEl)
                && requestedAtEl.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(requestedAtEl.GetString(), out var parsedRequestedAt))
            {
                startsAt = parsedRequestedAt;
            }
            else
            {
                return (requestedCapacity, null);
            }

            return (requestedCapacity, Derive(resourceId, serviceId, locationId, startsAt));
        }
        catch (JsonException)
        {
            return (1, null);
        }
    }

    private static string? TryGetString(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String)
        {
            var val = el.GetString();
            return string.IsNullOrEmpty(val) ? null : val;
        }

        return null;
    }
}
