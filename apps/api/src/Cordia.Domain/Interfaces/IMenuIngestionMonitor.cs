namespace Cordia.Domain.Interfaces;

/// <summary>
/// Settles vendor catalogs stuck mid-ingestion (docs §12 gap 8).
///
/// <para>It WRITES. The D4 amendment (owner ruling 2026-08-06) added <c>extract_failed</c> to the
/// <c>catalog_status</c> vocabulary, so a stalled catalog is now driven into a terminal state that
/// says it failed, rather than merely counted while it sat in <c>extracting</c> forever. Recovery
/// is a fresh upload, which supersedes.</para>
///
/// <para>This is the BACKSTOP, not the main path: a synchronous extraction failure is settled by
/// <c>IngestVendorMenuCommandHandler</c> at the moment it is detected. What reaches this monitor
/// is the case that handler cannot cover — a worker that died mid-extraction and never ran its
/// own catch block.</para>
/// </summary>
public interface IMenuIngestionMonitor
{
    /// <summary>
    /// Settles catalogs that ENTERED <c>scanning</c>/<c>extracting</c> before
    /// <paramref name="stalledBefore"/> and are still there. Returns how many were settled — not
    /// how many were found, since a row another writer moved first is correctly left alone.
    /// </summary>
    /// <param name="stalledBefore">
    /// Compared against the catalog's <c>status_changed_at</c>, NOT its creation time: a catalog
    /// whose ingest message waited in a queue backlog has been mid-ingestion for none of that
    /// wait, and failing it on arrival would discard an extraction that was still running.
    /// </param>
    /// <param name="tenantId">
    /// Narrows the sweep to one tenant. <c>null</c> — the production case — sweeps across all
    /// tenants, which is what a background monitor needs. The narrowing exists because a
    /// cross-tenant count is not a meaningful assertion in a shared database: a test that seeds one
    /// stalled catalog and asserts "at least one" would pass whether or not its own row was found.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<int> SettleStalledAsync(
        DateTimeOffset stalledBefore,
        string? tenantId = null,
        CancellationToken ct = default);
}
