using System.Text.RegularExpressions;

namespace Cordia.Domain.Security;

/// <summary>
/// The single allow-list for object-store path segments (tenant / purpose / object id) and the
/// ids that flow into them from the API surface (session ids on transcript endpoints, trace
/// ids). One source so the FluentValidation rules (→ 400) and the store's own fail-closed
/// guard (→ exception, defense-in-depth) can never drift: no separators, no leading dot, so
/// <c>..</c> and absolute paths cannot form (Inv-13 path isolation).
/// </summary>
public static partial class ObjectStoreSegment
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    public static partial Regex Pattern();

    // A ListIds prefix is a LEADING FRAGMENT of a valid segment: the SAME character class
    // (traversal-proof — leading char is alphanumeric, so a leading dot and separators cannot
    // form) but WITHOUT the 128-char length cap. An over-long prefix can never match a stored
    // (<=128-char) id, so it must yield an empty list — capping it would instead throw on a
    // boundary case (e.g. a 128-char session id + '.') and surface as a 500.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    public static partial Regex PrefixPattern();

    /// <summary>True iff the value is a valid single path segment.</summary>
    public static bool IsValid(string? value)
        => value is not null && Pattern().IsMatch(value);

    /// <summary>True iff the value is a valid ListIds prefix (segment character class, no
    /// length cap) — traversal-proof but tolerant of a fragment longer than any storable id.</summary>
    public static bool IsValidPrefix(string? value)
        => value is not null && PrefixPattern().IsMatch(value);
}
