using Cordia.Domain.Exceptions;
using Cordia.Infrastructure.Security;
using Microsoft.Extensions.Configuration;

namespace Cordia.Tests.UnitTests.Security;

[Trait("Category", "Unit")]
public class ConfigurationSecretStoreTests
{
    private static ConfigurationSecretStore MakeStore(params (string Key, string Value)[] secrets)
        => new(new ConfigurationBuilder()
            .AddInMemoryCollection(secrets.ToDictionary(
                s => $"Secrets:{s.Key}", s => (string?)s.Value))
            .Build());

    [Fact]
    public void GetSecret_ReturnsConfiguredValue()
    {
        var store = MakeStore(("StripeApiKey", "sk_test_123"));
        Assert.Equal("sk_test_123", store.GetSecret("StripeApiKey"));
    }

    [Fact]
    public void GetSecret_ReturnsNullWhenAbsent()
    {
        Assert.Null(MakeStore().GetSecret("Missing"));
    }

    [Fact]
    public void GetRequiredSecret_ThrowsWithNameButNeverAValue()
    {
        var store = MakeStore(("Present", "value-that-must-not-leak"));

        var ex = Assert.Throws<SecretNotFoundException>(() => store.GetRequiredSecret("Missing"));

        Assert.Equal("Missing", ex.Name);
        Assert.Contains("Missing", ex.Message);
        Assert.DoesNotContain("value-that-must-not-leak", ex.Message);
    }
}
