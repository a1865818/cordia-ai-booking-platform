using Cordia.Infrastructure.EntityFramework.Concurrency;

namespace Cordia.Tests.UnitTests.Concurrency;

[Trait("Category", "Unit")]
public class VersionTokenUpdateTests
{
    [Fact]
    public void ShouldReload_ReturnsTrue_WhenZeroRowsAffected() =>
        Assert.True(VersionTokenUpdate.ShouldReload(0));

    [Fact]
    public void ShouldReload_ReturnsFalse_WhenOneRowAffected() =>
        Assert.False(VersionTokenUpdate.ShouldReload(1));
}
