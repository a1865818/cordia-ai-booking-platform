using Cordia.Domain.Capacity;

namespace Cordia.Tests.UnitTests.Capacity;

[Trait("Category", "Unit")]
public class CapacitySlotKeyTests
{
    [Fact]
    public void Derive_UsesResourceId_WhenAllPresent()
    {
        var startsAt = new DateTimeOffset(2025, 6, 28, 10, 0, 0, TimeSpan.Zero);

        var key = CapacitySlotKey.Derive("res-1", "svc-1", "loc-1", startsAt);

        Assert.NotNull(key);
        Assert.StartsWith("res-1|", key);
    }

    [Fact]
    public void Derive_FallsBackToServiceId_WhenResourceIdNull()
    {
        var startsAt = new DateTimeOffset(2025, 6, 28, 10, 0, 0, TimeSpan.Zero);

        var key = CapacitySlotKey.Derive(null, "svc-1", "loc-1", startsAt);

        Assert.NotNull(key);
        Assert.StartsWith("svc-1|", key);
    }

    [Fact]
    public void Derive_FallsBackToLocationId_WhenResourceAndServiceIdNull()
    {
        var startsAt = new DateTimeOffset(2025, 6, 28, 10, 0, 0, TimeSpan.Zero);

        var key = CapacitySlotKey.Derive(null, null, "loc-1", startsAt);

        Assert.NotNull(key);
        Assert.StartsWith("loc-1|", key);
    }

    [Fact]
    public void Derive_ReturnsNull_WhenAllResourceDimensionsNullOrEmpty()
    {
        var startsAt = new DateTimeOffset(2025, 6, 28, 10, 0, 0, TimeSpan.Zero);

        Assert.Null(CapacitySlotKey.Derive(null, null, null, startsAt));
        Assert.Null(CapacitySlotKey.Derive("", "", "", startsAt));
        Assert.Null(CapacitySlotKey.Derive("", null, "", startsAt));
    }

    [Fact]
    public void Derive_EmptyResourceId_FallsBackToServiceId()
    {
        var startsAt = new DateTimeOffset(2025, 6, 28, 10, 0, 0, TimeSpan.Zero);

        var key = CapacitySlotKey.Derive("", "svc-1", null, startsAt);

        Assert.NotNull(key);
        Assert.StartsWith("svc-1|", key);
    }

    [Fact]
    public void Derive_StartsAtFormattedAsRoundTrip()
    {
        var startsAt = new DateTimeOffset(2025, 6, 28, 10, 30, 0, TimeSpan.FromHours(7));

        var key = CapacitySlotKey.Derive("res-1", null, null, startsAt);

        Assert.NotNull(key);
        var parts = key!.Split('|', 2);
        Assert.Equal(2, parts.Length);
        // The second part should be parseable back to the same DateTimeOffset
        var parsed = DateTimeOffset.Parse(parts[1], null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(startsAt, parsed);
    }

    [Fact]
    public void Derive_TwoCallsWithSameArgs_ReturnEqualKeys()
    {
        var startsAt = new DateTimeOffset(2025, 6, 28, 10, 0, 0, TimeSpan.Zero);

        var key1 = CapacitySlotKey.Derive("res-1", "svc-1", "loc-1", startsAt);
        var key2 = CapacitySlotKey.Derive("res-1", "svc-1", "loc-1", startsAt);

        Assert.Equal(key1, key2);
    }
}
