using System.Reflection;
using System.Text.Json.Serialization;

namespace Cordia.Domain.Enums;

/// <summary>
/// Snake_case wire name for any generated enum value.
///
/// The generated types carry the authoritative name on each member as
/// <see cref="JsonStringEnumMemberNameAttribute"/>; the CLR names are PascalCase, so a naive
/// <c>ToString().ToLowerInvariant()</c> is correct only for single-word members and silently
/// wrong for the rest — <c>NotRequired</c> becomes <c>"notrequired"</c> rather than the
/// declared <c>"not_required"</c>, which is not a value the schema permits.
///
/// Reading the attribute means a member added to an enum is picked up automatically, with no
/// second hand-maintained list to drift. <see cref="OperationStatusNames"/> does the same for
/// the D1 statuses and additionally parses them back.
/// </summary>
public static class EnumWireNames
{
    /// <summary>The declared wire name, falling back to lower-case when no attribute exists.</summary>
    public static string ToWireName<TEnum>(TEnum value) where TEnum : struct, Enum
        => typeof(TEnum)
               .GetField(value.ToString())
               ?.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()
               ?.Name
           ?? value.ToString().ToLowerInvariant();

    /// <summary>Nullable overload — <c>null</c> in, <c>null</c> out.</summary>
    public static string? ToWireName<TEnum>(TEnum? value) where TEnum : struct, Enum
        => value.HasValue ? ToWireName(value.Value) : null;
}
