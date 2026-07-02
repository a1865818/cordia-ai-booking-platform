namespace Cordia.Infrastructure.Repositories;

/// <summary>
/// Escapes user-supplied text for interpolation into a SQL <c>LIKE</c>/<c>ILIKE</c> pattern so it
/// matches literally: <c>%</c> and <c>_</c> are wildcards, and a pattern ENDING in the escape
/// character raises PostgreSQL error 22025 ("LIKE pattern must not end with escape character") —
/// an externally triggerable 500 when built from raw input.
/// </summary>
public static class SqlLikePattern
{
    /// <summary>Escape character passed to <c>ILIKE ... ESCAPE</c> (PostgreSQL's default).</summary>
    public const string EscapeCharacter = @"\";

    /// <summary>Escapes <c>\</c>, <c>%</c> and <c>_</c> in <paramref name="raw"/>.</summary>
    public static string Escape(string raw) =>
        raw.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
