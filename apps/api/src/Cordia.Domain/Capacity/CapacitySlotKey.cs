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
    /// <param name="startsAt">requested_at from canonical payload, as a <see cref="DateTimeOffset"/>.</param>
    /// <returns>
    /// A deterministic slot key string, or <see langword="null"/> if no resource dimension is present.
    /// </returns>
    public static string? Derive(
        string? resourceId,
        string? serviceId,
        string? locationId,
        DateTimeOffset startsAt)
    {
        var resource = FirstNonEmpty(resourceId, serviceId, locationId);
        if (resource is null)
            return null;

        return $"{resource}|{startsAt:O}";
    }

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrEmpty(candidate))
                return candidate;
        }

        return null;
    }
}
