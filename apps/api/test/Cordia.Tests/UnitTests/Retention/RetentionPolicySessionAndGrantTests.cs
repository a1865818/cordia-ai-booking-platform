using Cordia.Infrastructure.Services.Retention;

namespace Cordia.Tests.UnitTests.Retention;

/// <summary>Spec-14 retention helpers: session anonymization + grant pruning windows.</summary>
[Trait("Category", "Unit")]
public class RetentionPolicySessionAndGrantTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromDays(90);

    [Fact]
    public void LiveSession_IsNeverAnonymized()
    {
        Assert.False(RetentionPolicy.ShouldAnonymizeSession(null, Now, Window));
    }

    [Fact]
    public void EndedSession_InsideWindow_IsKept()
    {
        Assert.False(RetentionPolicy.ShouldAnonymizeSession(Now.AddDays(-89), Now, Window));
    }

    [Fact]
    public void EndedSession_PastWindow_IsAnonymized()
    {
        Assert.True(RetentionPolicy.ShouldAnonymizeSession(Now.AddDays(-91), Now, Window));
    }

    [Fact]
    public void ActiveGrant_WithFutureExpiry_IsNeverPruned()
    {
        Assert.False(RetentionPolicy.ShouldPruneGrant(
            expiresAt: Now.AddMinutes(10), consumedAt: null, Now, TimeSpan.FromDays(30)));
    }

    [Fact]
    public void ExpiredGrant_InsideWindow_IsKept()
    {
        Assert.False(RetentionPolicy.ShouldPruneGrant(
            expiresAt: Now.AddDays(-29), consumedAt: null, Now, TimeSpan.FromDays(30)));
    }

    [Fact]
    public void ExpiredGrant_PastWindow_IsPruned()
    {
        Assert.True(RetentionPolicy.ShouldPruneGrant(
            expiresAt: Now.AddDays(-31), consumedAt: null, Now, TimeSpan.FromDays(30)));
    }

    [Fact]
    public void ConsumedGrant_PastWindow_IsPruned_EvenWithLaterExpiry()
    {
        Assert.True(RetentionPolicy.ShouldPruneGrant(
            expiresAt: Now.AddDays(-1), consumedAt: Now.AddDays(-31), Now, TimeSpan.FromDays(30)));
    }
}
