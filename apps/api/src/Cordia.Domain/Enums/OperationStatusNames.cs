using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Cordia.Domain.Enums;

/// <summary>
/// Wire-name lookup for <see cref="OperationStatus"/>.
///
/// The 16 ratified values (D1) travel as snake_case both on the wire and in the pg-enum,
/// but the CLR names are PascalCase, so <c>Enum.TryParse</c> cannot round-trip them. The
/// map is built from the <see cref="JsonStringEnumMemberNameAttribute"/> already on each
/// member, so a value added to the enum is picked up automatically and there is no second
/// hand-maintained list to drift.
/// </summary>
public static class OperationStatusNames
{
    private static readonly FrozenDictionary<string, OperationStatus> ByWireName = BuildMap();

    /// <summary>Every ratified wire name, in declaration order.</summary>
    public static IReadOnlyList<string> All { get; } =
        [.. Enum.GetValues<OperationStatus>().Select(ToWireName)];

    /// <summary>The snake_case wire name for a status.</summary>
    public static string ToWireName(OperationStatus status)
        => typeof(OperationStatus)
               .GetField(status.ToString())
               ?.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()
               ?.Name
           ?? status.ToString();

    /// <summary>Parses a snake_case wire name; case-insensitive, false when unknown.</summary>
    public static bool TryParse(string? value, out OperationStatus status)
    {
        status = default;
        return !string.IsNullOrWhiteSpace(value) && ByWireName.TryGetValue(value.Trim(), out status);
    }

    private static FrozenDictionary<string, OperationStatus> BuildMap()
        => Enum.GetValues<OperationStatus>()
            .ToDictionary(ToWireName, s => s, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
}
