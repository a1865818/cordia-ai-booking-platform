using System.Reflection;

namespace Cordia.Infrastructure.Repositories;

/// <summary>
/// Resolves the authoritative PostgreSQL enum label for a CLR enum value, for use when writing
/// pg-enum columns via raw <c>INSERT … ON CONFLICT</c> where Npgsql's enum type-mapper is not engaged.
/// </summary>
internal static class PgEnum
{
    /// <summary>
    /// Returns the label from the enum member's <c>[PgName]</c> attribute, falling back to
    /// <c>ToString().ToLowerInvariant()</c> only when the attribute is absent — so multi-word
    /// names like <c>InProgress</c> → <c>"in_progress"</c> are always sourced from the declared
    /// <c>[PgName("in_progress")]</c> rather than a naive lower-case transform.
    /// </summary>
    public static string Label<TEnum>(TEnum value) where TEnum : struct, Enum
        => typeof(TEnum).GetField(value.ToString())!
            .GetCustomAttribute<NpgsqlTypes.PgNameAttribute>()?.PgName
            ?? value.ToString().ToLowerInvariant();
}
