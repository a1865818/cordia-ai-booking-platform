using Cordia.Domain.Interfaces;
using Cordia.Domain.Observability;
using Cordia.Infrastructure.Services;

namespace Cordia.Tests.UnitTests.Observability;

[Trait("Category", "Unit")]
public class CorrelationHolderTests
{
    [Fact]
    public void Set_ThenCurrent_ReturnsTheSetContext()
    {
        ICorrelationContext holder = new CorrelationContextHolder();
        var ctx = new AmbientCorrelationContext("tenant-1", "corr_abc", CausationId: "cause-1");

        holder.Set(ctx);

        Assert.Same(ctx, holder.Current);
        Assert.Equal("corr_abc", holder.Current.CorrelationId);
    }

    [Fact]
    public void CurrentOrNull_ReflectsSetValue_InSameAsyncContext()
    {
        var holder = new CorrelationContextHolder();
        var ctx = new AmbientCorrelationContext("tenant-2", "corr_xyz");

        holder.Set(ctx);

        Assert.NotNull(CorrelationContextHolder.CurrentOrNull);
        Assert.Equal("corr_xyz", CorrelationContextHolder.CurrentOrNull!.CorrelationId);
    }

    [Fact]
    public async Task Current_WhenUnset_ReturnsEmpty()
    {
        // Run in an isolated async flow so prior tests' AsyncLocal values don't bleed in.
        await Task.Run(() =>
        {
            ICorrelationContext holder = new CorrelationContextHolder();
            Assert.Same(AmbientCorrelationContext.Empty, holder.Current);
        }, TestContext.Current.CancellationToken);
    }
}
