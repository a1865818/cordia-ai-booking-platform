namespace Cordia.Domain.Fulfillment;

/// <summary>
/// Derives a stable, deterministic provider idempotency key for an operation step (Inv-2/3/7:
/// same key across retries for the same step+revision; a material revision yields a new key).
/// </summary>
public static class ProviderIdempotencyKey
{
    /// <summary>Deterministically derives the STABLE provider idempotency key for an operation's step
    /// (Inv-2/3/7: same across retries, never minted per attempt). Bound to operation_id + step_type +
    /// intent_revision so a material revision (new revision) yields a new key, but retries of the same
    /// step+revision reuse it. Returns "pik_" + lowercase SHA-256 hex of "operationId|stepType|intentRevision".</summary>
    public static string Derive(string operationId, string stepType, int intentRevision)
    {
        var input = global::System.Text.Encoding.UTF8.GetBytes($"{operationId}|{stepType}|{intentRevision}");
        var hash = global::System.Security.Cryptography.SHA256.HashData(input);
        return "pik_" + global::System.Convert.ToHexString(hash).ToLowerInvariant();
    }
}
