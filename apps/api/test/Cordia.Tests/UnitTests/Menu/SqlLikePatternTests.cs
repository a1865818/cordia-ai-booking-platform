using Cordia.Infrastructure.Repositories;

namespace Cordia.Tests.UnitTests.Menu;

/// <summary>
/// Catalog search builds an ILIKE pattern from raw customer text (spec 11 §7 foreground read
/// path). Unescaped metacharacters turn the "narrow keyword search" into match-everything, and a
/// trailing backslash raises PG error 22025 → HTTP 500 (review PR #65, finding 6).
/// </summary>
[Trait("Category", "Unit")]
public class SqlLikePatternTests
{
    [Theory]
    [InlineData("burger", "burger")]
    [InlineData("100%", @"100\%")]
    [InlineData("mac_and_cheese", @"mac\_and\_cheese")]
    [InlineData(@"\", @"\\")]
    [InlineData(@"50%_\", @"50\%\_\\")]
    public void Escape_NeutralizesLikeMetacharacters(string raw, string expected)
    {
        Assert.Equal(expected, SqlLikePattern.Escape(raw));
    }

    [Fact]
    public void Escape_NeverEndsWithBareEscapeCharacter()
    {
        // PG error 22025: a pattern ending in the escape character is invalid — the escaped
        // output must always pair a trailing backslash.
        var escaped = SqlLikePattern.Escape(@"query\");
        Assert.EndsWith(@"\\", escaped);
    }
}
