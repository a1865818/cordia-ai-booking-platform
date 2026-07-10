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

    /// <summary>True iff the value is a valid single path segment.</summary>
    public static bool IsValid(string? value)
        => value is not null && Pattern().IsMatch(value);
}
