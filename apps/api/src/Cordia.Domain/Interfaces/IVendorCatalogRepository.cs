using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Menu;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Read access to the vendor catalog. Both methods return items from the
/// <b>published</b> catalog only (spec 11 §5.2, §7, Inv-16) and are tenant-scoped
/// through the <c>ApplicationDbContext</c> global query filter (Inv-13).
/// Draft / in-review catalogs are never visible here.
/// </summary>
public interface IVendorCatalogRepository
{
    /// <summary>
    /// Paged, case-insensitive search over published catalog items, matching
    /// <paramref name="query"/> against name/description (all items when
    /// <paramref name="query"/> is null/blank) and, when <paramref name="category"/> is
    /// supplied, narrowed to that exact category (§4.2 / foreground <c>category</c> filter).
    /// Ordered by name for stable paging.
    /// </summary>
    Task<IReadOnlyList<VendorCatalogItem>> SearchPublishedItemsAsync(
        string? query, string? category, int skip, int take, CancellationToken ct = default);

    /// <summary>
    /// Single published item by id, or <c>null</c> when no published item with that
    /// id exists for the current tenant (caller maps null → 404).
    /// </summary>
    Task<VendorCatalogItem?> GetPublishedItemAsync(string itemId, CancellationToken ct = default);

    /// <summary>
    /// Batch lookup of published items by id (ONE query), for callers that check many lines at
    /// once — e.g. pre-order live revalidation inside the inbox transaction (spec 11 §7: snapshot
    /// p95 &lt; 150 ms, not one round-trip per line). Ids with no published item are absent from
    /// the result.
    /// </summary>
    Task<IReadOnlyList<VendorCatalogItem>> GetPublishedItemsAsync(
        IReadOnlyCollection<string> itemIds, CancellationToken ct = default);

    /// <summary>
    /// All items on a catalog, at ANY status — the draft-scoped counterpart to
    /// <see cref="GetPublishedItemsAsync"/>. Returns the namespaced <c>{catalogId}:{rawId}</c>
    /// ids that <c>POST /menu/catalogs/{id}/edit</c> keys on, which the published-scoped reads
    /// cannot surface while a catalog is in_review. Tenant-scoped, AsNoTracking.
    /// </summary>
    Task<IReadOnlyList<VendorCatalogItem>> GetDraftItemsAsync(
        string catalogId, CancellationToken ct = default);

    /// <summary>
    /// Does <paramref name="catalogId"/> have at least one item, at ANY status? An EXISTENCE
    /// question, so callers that only need the yes/no — publish's itemless-catalog guard (§5.2)
    /// — never materialise a whole menu (rows, descriptions and jsonb blobs) to read a count.
    /// Any status, like <see cref="GetDraftItemsAsync"/>, which is what publish wants: an
    /// approved catalog's items are frozen. Tenant-scoped via the global query filter (Inv-13).
    /// </summary>
    Task<bool> HasAnyItemsAsync(string catalogId, CancellationToken ct = default);

    /// <summary>
    /// The tenant's catalogs, newest first, at any status.
    ///
    /// This is the DISCOVERY read: without it the console could only ever show catalogs started
    /// from its own session, so an ingestion begun elsewhere (or before a refresh) was invisible
    /// — docs §12 gap 3. Tenant-scoped, AsNoTracking.
    /// </summary>
    Task<IReadOnlyList<VendorCatalog>> ListAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Loads a catalog by id (any status), tenant-scoped via the global query filter,
    /// or <c>null</c> when absent. Used by the ingestion / review lifecycle handlers.
    /// </summary>
    Task<VendorCatalog?> GetAsync(string catalogId, CancellationToken ct = default);

    /// <summary>
    /// Version-token conditional status transition (§22.1, §5.2): sets <paramref name="next"/>
    /// only when the row is in <paramref name="expectedStatus"/> AND at
    /// <paramref name="expectedVersion"/>. Returns rows-affected (1 = success, 0 = lost race).
    /// </summary>
    Task<int> TryTransitionStatusAsync(
        string tenantId,
        string catalogId,
        long expectedVersion,
        CatalogStatus expectedStatus,
        CatalogStatus next,
        CancellationToken ct = default);

    /// <summary>Inserts a new catalog row (owner upload, status <c>uploaded</c>). Flushes within the ambient transaction.</summary>
    Task AddCatalogAsync(VendorCatalog catalog, CancellationToken ct = default);

    /// <summary>Inserts draft catalog items (extraction output). Flushes within the ambient transaction.</summary>
    Task AddDraftItemsAsync(IEnumerable<VendorCatalogItem> items, CancellationToken ct = default);

    /// <summary>
    /// The tenant's currently-<see cref="CatalogStatus.Published"/> catalog other than
    /// <paramref name="excludeCatalogId"/>, or <c>null</c> when none is live. Used by publish to
    /// supersede the prior live version (only one published catalog is live per tenant at a time).
    /// Tenant-scoped via the global query filter (Inv-13).
    /// </summary>
    Task<VendorCatalog?> GetCurrentPublishedAsync(string excludeCatalogId, CancellationToken ct = default);

    /// <summary>
    /// Version-token conditional approve (§22.1, §5.2): when the row is in
    /// <see cref="CatalogStatus.InReview"/> at <paramref name="expectedVersion"/>, sets status →
    /// <see cref="CatalogStatus.Approved"/> AND records <paramref name="approvedBy"/> /
    /// <paramref name="approvedAt"/> in one atomic statement. Returns rows-affected (1 = won, 0 = lost race).
    /// </summary>
    Task<int> TryApproveAsync(
        string tenantId,
        string catalogId,
        long expectedVersion,
        string approvedBy,
        DateTimeOffset approvedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Applies owner edits to draft catalog items (spec 11 §5.2) within the ambient transaction. Each
    /// edit changes only its non-null fields and is keyed on the FULL namespaced id the read path emits
    /// (<c>{catalogId}:{rawId}</c>); only items whose catalog is still <c>in_review</c> are editable.
    /// An edit that matches no editable row throws <see cref="Exceptions.CatalogItemNotFoundException"/>
    /// (never a silent no-op).
    /// </summary>
    Task ApplyItemEditsAsync(string catalogId, IEnumerable<CatalogItemEdit> edits, CancellationToken ct = default);

    /// <summary>
    /// Clears the draft-only extraction-agent metadata (<c>extraction_confidence</c> /
    /// <c>source_location</c>) on all items of <paramref name="catalogId"/> — called on owner approval so
    /// a published catalog row never retains extraction provenance (§3, Inv-16). Runs within the ambient
    /// transaction.
    /// </summary>
    Task ClearDraftExtractionMetadataAsync(string catalogId, CancellationToken ct = default);
}
