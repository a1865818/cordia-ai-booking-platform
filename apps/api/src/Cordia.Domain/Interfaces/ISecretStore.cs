namespace Cordia.Domain.Interfaces;

/// <summary>
/// Server-side custody of provider credentials (spec 14 §24.4, Inv-12).
/// Secrets are injected into provider adapters through this seam only — they
/// must never be serialized into queue envelopes, prompts, traces, audit
/// metadata, or exception messages. Implementations must not log values.
/// </summary>
public interface ISecretStore
{
    /// <summary>Returns the secret value, or null when not configured.</summary>
    string? GetSecret(string name);

    /// <summary>Returns the secret value or throws <c>SecretNotFoundException</c>.</summary>
    string GetRequiredSecret(string name);
}
