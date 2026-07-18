namespace Cordia.Domain.Interfaces;

/// <summary>
/// The spec-14 privacy retention entry point (§24.3, spec 12 §6.3): sweeps
/// every tenant's expired idempotency records (prune), ended conversation
/// sessions (anonymize), and consumed/expired verification grants (prune) —
/// each action audited. Domain seam so the Infrastructure hosted service and
/// tests share one implementation.
/// </summary>
public interface IRetentionSweeper
{
    /// <summary>
    /// Runs one full sweep cycle across all tenants. Returns the total number
    /// of rows pruned/anonymized. Per-tenant failures are logged and skipped —
    /// one tenant's bad data never blocks another's retention.
    /// </summary>
    Task<int> SweepAllTenantsAsync(DateTimeOffset now, CancellationToken ct = default);
}
