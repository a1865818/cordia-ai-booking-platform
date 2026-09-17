#pragma warning disable xUnit1051
using Cordia.Domain.Capacity;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Services;
using Moq;

namespace Cordia.Tests.UnitTests.Capacity;

/// <summary>
/// Unit tests for <see cref="CapacityPrimitiveResolver"/> (spec 07 §6.3, §5, §13; #106).
///
/// <para>These used to assert that every (tenant, provider) resolved to first-party — i.e. they
/// pinned the hardcoding #106 removes. They now pin the configured lookup and its two fallbacks.</para>
/// </summary>
[Trait("Category", "Unit")]
public class CapacityPrimitiveResolverTests
{
    private readonly Mock<ITenantCapacityPrimitiveRepository> _configured = new();
    private CapacityPrimitiveResolver Sut => new(_configured.Object);

    [Theory]
    [InlineData(CapacityPrimitive.NativeHold)]
    [InlineData(CapacityPrimitive.IdempotentPending)]
    [InlineData(CapacityPrimitive.AtomicBook)]
    [InlineData(CapacityPrimitive.CheckThenCreate)]
    public async Task Resolve_ReturnsTheConfiguredRung_ForAnExternalProvider(CapacityPrimitive configured)
    {
        _configured.Setup(r => r.GetAsync("tenant-1", "acme_reservations", It.IsAny<CancellationToken>()))
            .ReturnsAsync(configured);

        Assert.Equal(configured, await Sut.ResolveAsync("tenant-1", "acme_reservations", "res-1"));
    }

    [Fact]
    public async Task Resolve_FallsBackToFirstParty_WhenNothingIsConfigured()
    {
        _configured.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CapacityPrimitive?)null);

        Assert.Equal(CapacityPrimitive.FirstParty, await Sut.ResolveAsync("tenant-2", "acme_reservations", null));
    }

    /// <summary>A configuration is per tenant: another tenant's row must not apply.</summary>
    [Fact]
    public async Task Resolve_AsksForTheRequestingTenantsRow()
    {
        _configured.Setup(r => r.GetAsync("alpha", "acme_reservations", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CapacityPrimitive.AtomicBook);

        Assert.Equal(CapacityPrimitive.FirstParty, await Sut.ResolveAsync("beta", "acme_reservations", null));
    }

    /// <summary>
    /// First-party inventory is the conditional UPDATE on capacity_slots and has no other rung. A row
    /// claiming atomic booking for it would make a first-party hold report "confirmed" (Inv-16), so
    /// it is not even consulted.
    /// </summary>
    [Fact]
    public async Task Resolve_KeepsTheFirstPartyProviderOnTheFirstPartyRung_WhateverIsConfigured()
    {
        _configured.Setup(r => r.GetAsync(It.IsAny<string>(), CapacityProviders.FirstParty, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CapacityPrimitive.AtomicBook);

        Assert.Equal(CapacityPrimitive.FirstParty, await Sut.ResolveAsync("tenant-3", CapacityProviders.FirstParty, "res-9"));
        _configured.Verify(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
        => Assert.Equal(expected, Sut.ModeFor(primitive));

    [Fact]
    public void ModeFor_Throws_ForUnknownPrimitive()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Sut.ModeFor((CapacityPrimitive)999));
}
