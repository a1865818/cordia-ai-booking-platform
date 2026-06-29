using Cordia.Domain.Fulfillment;

namespace Cordia.Tests.UnitTests.Fulfillment;

[Trait("Category", "Unit")]
public class ProviderIdempotencyKeyTests
{
    // (1) Same inputs always produce the same key — idempotent derivation
    [Fact]
    public void Derive_SameInputs_ReturnsSameKey()
    {
        var key1 = ProviderIdempotencyKey.Derive("op-abc", "capacity_hold", 1);
        var key2 = ProviderIdempotencyKey.Derive("op-abc", "capacity_hold", 1);

        Assert.Equal(key1, key2);
    }

    // (2) Different operation → different key
    [Fact]
    public void Derive_DifferentOperation_ReturnsDifferentKey()
    {
        var key1 = ProviderIdempotencyKey.Derive("op-abc", "capacity_hold", 1);
        var key2 = ProviderIdempotencyKey.Derive("op-xyz", "capacity_hold", 1);

        Assert.NotEqual(key1, key2);
    }

    // (3) Different step type → different key
    [Fact]
    public void Derive_DifferentStepType_ReturnsDifferentKey()
    {
        var key1 = ProviderIdempotencyKey.Derive("op-abc", "capacity_hold", 1);
        var key2 = ProviderIdempotencyKey.Derive("op-abc", "payment", 1);

        Assert.NotEqual(key1, key2);
    }

    // (4) Different revision → different key (material revision yields new key per Inv-2/3)
    [Fact]
    public void Derive_DifferentRevision_ReturnsDifferentKey()
    {
        var key1 = ProviderIdempotencyKey.Derive("op-abc", "capacity_hold", 1);
        var key2 = ProviderIdempotencyKey.Derive("op-abc", "capacity_hold", 2);

        Assert.NotEqual(key1, key2);
    }

    // (5) Format: starts with "pik_" followed by 64 lowercase hex characters (SHA-256)
    [Fact]
    public void Derive_ResultFormat_StartsWithPrefixAndIsLowercaseHex()
    {
        var key = ProviderIdempotencyKey.Derive("op-abc", "capacity_hold", 1);

        Assert.StartsWith("pik_", key);
        var hex = key[4..]; // strip "pik_"
        Assert.Equal(64, hex.Length);
        Assert.All(hex, c => Assert.True(char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f'),
            $"Non-hex character: {c}"));
    }

    // (6) Exact expected hash for a fixed input — catches any future change in derivation algorithm
    [Fact]
    public void Derive_FixedInput_ReturnsKnownHash()
    {
        // SHA-256("op-abc|capacity_hold|1") = 19f5807d1b69db4554c85c9b2d4ded26b3617b2ffb7cb1886d2c1b9f4a5c7ca5
        const string expected = "pik_19f5807d1b69db4554c85c9b2d4ded26b3617b2ffb7cb1886d2c1b9f4a5c7ca5";

        var actual = ProviderIdempotencyKey.Derive("op-abc", "capacity_hold", 1);

        Assert.Equal(expected, actual);
    }
}
