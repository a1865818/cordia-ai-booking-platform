using Cordia.Domain.Capacity;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.Capacity;

namespace Cordia.Tests.UnitTests.Capacity;

/// <summary>
/// Unit tests for <see cref="CapacityPrimitiveResolver"/> (spec 07 §6.3, §5, §13).
/// Verifies ladder selection and mode-mapping without any I/O.
/// </summary>
[Trait("Category", "Unit")]
public class CapacityPrimitiveResolverTests
{
    private readonly CapacityPrimitiveResolver _sut = new();

    // -----------------------------------------------------------------------
    // Resolve — always returns FirstParty (open-decision §13 default)
    // -----------------------------------------------------------------------

    [Fact]
    public void Resolve_ReturnsFirstParty_ForTypicalTenantAndProvider()
    {
        var result = _sut.Resolve("tenant-1", "first_party", "res-1");

        Assert.Equal(CapacityPrimitive.FirstParty, result);
    }

    [Fact]
    public void Resolve_ReturnsFirstParty_WhenSlotSourceIsNull()
    {
        var result = _sut.Resolve("tenant-2", "stub_external", null);

        Assert.Equal(CapacityPrimitive.FirstParty, result);
    }

    [Fact]
    public void Resolve_ReturnsFirstParty_ForDifferentTenants()
    {
        Assert.Equal(CapacityPrimitive.FirstParty, _sut.Resolve("alpha", "first_party", null));
        Assert.Equal(CapacityPrimitive.FirstParty, _sut.Resolve("beta", "acme_provider", "svc-99"));
        Assert.Equal(CapacityPrimitive.FirstParty, _sut.Resolve("gamma", "unknown_provider", "loc-7"));
    }

    // -----------------------------------------------------------------------
    // ModeFor — §5 mode-mapping table (one InlineData per rung)
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(CapacityPrimitive.FirstParty, AvailabilityModeEnum.Held)]
    [InlineData(CapacityPrimitive.NativeHold, AvailabilityModeEnum.Held)]
    [InlineData(CapacityPrimitive.IdempotentPending, AvailabilityModeEnum.Held)]
    [InlineData(CapacityPrimitive.AtomicBook, AvailabilityModeEnum.Confirmed)]
    [InlineData(CapacityPrimitive.CheckThenCreate, AvailabilityModeEnum.Live)]
    public void ModeFor_ReturnsExpectedMode(CapacityPrimitive primitive, AvailabilityModeEnum expected)
    {
        var mode = _sut.ModeFor(primitive);

        Assert.Equal(expected, mode);
    }

    [Fact]
    public void ModeFor_Throws_ForUnknownPrimitive()
    {
        // Cast an out-of-range int to exercise the exhaustive switch guard.
        var unknown = (CapacityPrimitive)999;

        Assert.Throws<ArgumentOutOfRangeException>(() => _sut.ModeFor(unknown));
    }
}
