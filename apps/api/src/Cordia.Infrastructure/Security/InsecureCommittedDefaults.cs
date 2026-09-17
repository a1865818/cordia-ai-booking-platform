using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Cordia.Infrastructure.Security;

/// <summary>
/// Finds credentials committed to this repository that are in effect in the running configuration
/// (#73). Outside Development, <c>Program.cs</c> refuses to start while any are.
///
/// <para><b>Why detection and not deletion.</b> Stripping the values was the filed remedy, and it
/// breaks two things: <c>JwsTenantContextSigner</c> is a singleton whose constructor throws on a
/// missing key, so the host crashes at first resolution; and the dev signing key is byte-coupled to
/// <c>apps/langgraph/.env.example</c>, so changing one side alone breaks agent↔API verification with
/// a wrong-signature error rather than a missing-config one. The values stay where developers need
/// them; what changes is that a deployment can no longer boot on them.</para>
///
/// <para><b>Only setting names ever leave this class.</b> The values are public already, but a log
/// line that prints a credential teaches people to paste real ones next to it (Inv-12).</para>
/// </summary>
public static class InsecureCommittedDefaults
{
    /// <summary>Explicit opt-in for hosts that run on the committed values outside Development.</summary>
    public const string AllowSettingKey = "Security:AllowInsecureCommittedDefaults";

    private const string ConnectionStringKey = "ConnectionStrings:CordiaDBConnection";

    /// <summary><c>appsettings.json</c> and <c>docker-compose.yml</c>.</summary>
    private const string CommittedDatabasePassword = "localdev";

    /// <summary>Development and Test signing key (decodes to a sentence saying it is not a secret).</summary>
    private const string CommittedSigningKey = "REVWLU9OTFktdGVuYW50LWNvbnRleHQtc2lnbmluZy1rZXktbm90LWEtcmVhbC1zZWNyZXQ=";

    /// <summary><c>appsettings.Development.json</c>.</summary>
    private const string CommittedWebhookSecret = "dev-notification-webhook-secret";

    /// <summary><c>appsettings.Test.json</c>.</summary>
    private const string CommittedObjectStoreKey = "dGVzdC1vbmx5LWtleS10ZXN0LW9ubHkta2V5LTEyMzQ=";

    /// <summary>Returns the configuration keys whose values are committed defaults. Never the values.</summary>
    public static IReadOnlyList<string> FindIn(IConfiguration configuration)
    {
        var found = new List<string>();

        if (HasCommittedDatabasePassword(configuration[ConnectionStringKey]))
            found.Add(ConnectionStringKey);

        foreach (var key in configuration.GetSection("Secrets:TenantContextSigningKeys:Keys").GetChildren())
        {
            if (Matches(key.Value, CommittedSigningKey))
                found.Add(key.Path);
        }

        foreach (var path in new[] { "Secrets:NotificationsWebhookSecret", "Notifications:WebhookSecret" })
        {
            if (Matches(configuration[path], CommittedWebhookSecret))
                found.Add(path);
        }

        if (Matches(configuration["Secrets:ObjectStoreEncryptionKey"], CommittedObjectStoreKey))
            found.Add("Secrets:ObjectStoreEncryptionKey");

        return found;
    }

    private static bool Matches(string? configured, string committed)
        => configured is not null && string.Equals(configured.Trim(), committed, StringComparison.Ordinal);

    private static bool HasCommittedDatabasePassword(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return false;

        try
        {
            return string.Equals(
                new NpgsqlConnectionStringBuilder(connectionString).Password,
                CommittedDatabasePassword,
                StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            // A malformed connection string is Npgsql's to report, with its own message; guessing
            // at its password here would only produce a worse one.
            return false;
        }
    }
}
