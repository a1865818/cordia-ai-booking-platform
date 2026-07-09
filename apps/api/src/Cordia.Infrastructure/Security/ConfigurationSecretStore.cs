using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Cordia.Infrastructure.Security;

/// <summary>
/// Configuration/environment-backed <see cref="ISecretStore"/> (spec 14 §24.4).
/// Reads the <c>Secrets</c> configuration section (bind real values via
/// environment variables / user-secrets / a vault provider in deployment —
/// never appsettings.json committed to the repo). Values are returned to the
/// caller only; nothing is logged or serialized here.
/// </summary>
public sealed class ConfigurationSecretStore(IConfiguration configuration) : ISecretStore
{
    public string? GetSecret(string name)
        => configuration.GetSection("Secrets").GetValue<string?>(name);

    public string GetRequiredSecret(string name)
        => GetSecret(name) ?? throw new SecretNotFoundException(name);
}
