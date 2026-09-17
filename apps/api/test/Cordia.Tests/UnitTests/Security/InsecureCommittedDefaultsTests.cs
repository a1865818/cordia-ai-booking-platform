using Cordia.Infrastructure.Security;
using Microsoft.Extensions.Configuration;

namespace Cordia.Tests.UnitTests.Security;

/// <summary>
/// Committed credentials are detected by setting name so the host can refuse to start on them
/// outside Development (#73).
/// </summary>
[Trait("Category", "Unit")]
public class InsecureCommittedDefaultsTests
{
    private const string CommittedSigningKey = "REVWLU9OTFktdGVuYW50LWNvbnRleHQtc2lnbmluZy1rZXktbm90LWEtcmVhbC1zZWNyZXQ=";

    private static IReadOnlyList<string> Find(Dictionary<string, string?> values)
        => InsecureCommittedDefaults.FindIn(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void TheCommittedDatabasePassword_IsFound()
        => Assert.Equal(
            ["ConnectionStrings:CordiaDBConnection"],
            Find(new() { ["ConnectionStrings:CordiaDBConnection"] = "Host=db;Username=cordia;Password=localdev" }));

    /// <summary>Parsed, not substring-matched: a real password that merely contains the word passes.</summary>
    [Fact]
    public void ARealDatabasePassword_IsNotFound()
        => Assert.Empty(Find(new()
        {
            ["ConnectionStrings:CordiaDBConnection"] = "Host=db;Username=cordia;Password=localdev-but-rotated-9f2c",
        }));

    [Fact]
    public void TheCommittedSigningKey_IsFound_UnderWhateverKidItIsFiledAs()
        => Assert.Equal(
            ["Secrets:TenantContextSigningKeys:Keys:prod-2026-09"],
            Find(new() { ["Secrets:TenantContextSigningKeys:Keys:prod-2026-09"] = CommittedSigningKey }));

    [Theory]
    [InlineData("Secrets:NotificationsWebhookSecret")]
    [InlineData("Notifications:WebhookSecret")]
    public void TheCommittedWebhookSecret_IsFound_UnderEitherKey(string key)
        => Assert.Equal([key], Find(new() { [key] = "dev-notification-webhook-secret" }));

    [Fact]
    public void TheCommittedObjectStoreKey_IsFound()
        => Assert.Equal(
            ["Secrets:ObjectStoreEncryptionKey"],
            Find(new() { ["Secrets:ObjectStoreEncryptionKey"] = "dGVzdC1vbmx5LWtleS10ZXN0LW9ubHkta2V5LTEyMzQ=" }));

    [Fact]
    public void AConfigurationWithRealSecrets_HasNothingToReport()
        => Assert.Empty(Find(new()
        {
            ["ConnectionStrings:CordiaDBConnection"] = "Host=db;Username=cordia;Password=s3cr3t-from-vault",
            ["Secrets:TenantContextSigningKeys:Keys:prod"] = "cmVhbC1rZXk=",
            ["Secrets:NotificationsWebhookSecret"] = "whsec_real",
            ["Secrets:ObjectStoreEncryptionKey"] = "cmVhbC1vYmplY3Qta2V5",
        }));

    /// <summary>What is reported is the setting's name. The credential itself must never appear.</summary>
    [Fact]
    public void Findings_NeverContainTheValues()
    {
        var found = Find(new()
        {
            ["ConnectionStrings:CordiaDBConnection"] = "Host=db;Password=localdev",
            ["Secrets:TenantContextSigningKeys:Keys:dev"] = CommittedSigningKey,
        });

        Assert.Equal(2, found.Count);
        Assert.DoesNotContain(found, f => f.Contains("localdev") || f.Contains(CommittedSigningKey));
    }
}
