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

    /// <summary>
    /// A whitespace-only dimension is ABSENT, matching what CreateCapacitySlotValidator admits.
    ///
    /// <para>While this used <c>IsNullOrEmpty</c>, <c>"  "</c> counted as a real dimension: the
    /// declared slot got the key <c>"  |{startsAt}"</c> while the booking path, deriving from a
    /// canonical payload carrying the meaningful dimension, derived a different one. The slot was
    /// stored, reported by <c>GET /api/availability</c> as declared capacity, and matched by
    /// nothing — the silent no-op this type exists to prevent.</para>
    /// </summary>
    [Fact]
    public void Derive_WhitespaceOnlyDimension_FallsThroughToTheNextOne()
    {
        var startsAt = new DateTimeOffset(2025, 6, 28, 10, 0, 0, TimeSpan.Zero);

        // Built from char codes rather than written as escapes, so the intent survives any
        // tooling that would silently rewrite a tab or newline literal in this file.
        string[] blanks = [" ", "   ", new string((char)9, 1), new string((char)10, 1)];

        foreach (var blank in blanks)
        {
            var key = CapacitySlotKey.Derive(blank, "svc-1", null, startsAt);

            Assert.NotNull(key);
            Assert.StartsWith("svc-1|", key);
        }
    }

    /// <summary>
    /// The key identifies an INSTANT, not a way of writing one.
    ///
    /// <para>The console declares slots with `toISOString()` (always `+00:00`); a canonical payload
    /// from the agent lane can carry the venue's local offset. With an offset-preserving format the
    /// two produce different keys for the same moment, so a declared slot never matches the booking
    /// it exists for — the reserve path silently takes the untracked branch and the venue's
    /// declared capacity is not enforced, with nothing reporting it.</para>
    /// </summary>
    [Fact]
    public void Derive_SameInstantDifferentOffsets_ProducesTheSameKey()
    {
        var utc = new DateTimeOffset(2026, 8, 31, 23, 0, 0, TimeSpan.Zero);
        var sydney = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.FromHours(10));

        // Same moment, written two ways.
        Assert.Equal(utc.UtcDateTime, sydney.UtcDateTime);
        Assert.Equal(
            CapacitySlotKey.Derive("res-1", null, null, utc),
            CapacitySlotKey.Derive("res-1", null, null, sydney));
    }

    /// <summary>Different instants must still key differently — the normalisation must not flatten them.</summary>
    [Fact]
    public void Derive_DifferentInstants_ProduceDifferentKeys()
    {
        var a = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
        var b = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.FromHours(10));

        Assert.NotEqual(
            CapacitySlotKey.Derive("res-1", null, null, a),
            CapacitySlotKey.Derive("res-1", null, null, b));
    }

    [Fact]
    public void Derive_AllDimensionsWhitespace_IsNotFirstPartyTracked()
    {
        var startsAt = new DateTimeOffset(2025, 6, 28, 10, 0, 0, TimeSpan.Zero);

        Assert.Null(CapacitySlotKey.Derive("  ", "  ", "  ", startsAt));
    }

    /// <summary>
    /// Padding must not create a second key for the same slot: the reserve path derives from a
    /// canonical payload, and an untrimmed declaration would sit beside it unmatched forever.
    /// </summary>
    [Fact]
    public void Derive_TrimsTheWinningDimension()
    {
        var startsAt = new DateTimeOffset(2025, 6, 28, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            CapacitySlotKey.Derive("res-1", null, null, startsAt),
            CapacitySlotKey.Derive("  res-1  ", null, null, startsAt));
    }

    /// <summary>
    /// The stored dimension and the key derived from it come from ONE resolution, so they cannot
    /// disagree about which dimension won.
    /// </summary>
    [Fact]
    public void ResolveResource_AgreesWithTheKeyItProduces()
    {
        var startsAt = new DateTimeOffset(2025, 6, 28, 10, 0, 0, TimeSpan.Zero);

        var resource = CapacitySlotKey.ResolveResource("  ", "svc-1", "loc-1");
        var key = CapacitySlotKey.Derive("  ", "svc-1", "loc-1", startsAt);

        Assert.Equal("svc-1", resource);
        Assert.StartsWith($"{resource}|", key);
    }
}
