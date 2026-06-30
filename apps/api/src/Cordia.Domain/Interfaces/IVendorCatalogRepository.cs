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
    /// <paramref name="query"/> is null/blank). Ordered by name for stable paging.
    /// </summary>
    Task<IReadOnlyList<VendorCatalogItem>> SearchPublishedItemsAsync(
        string? query, int skip, int take, CancellationToken ct = default);

    /// <summary>
    /// Single published item by id, or <c>null</c> when no published item with that
    /// id exists for the current tenant (caller maps null → 404).
    /// </summary>
    Task<VendorCatalogItem?> GetPublishedItemAsync(string itemId, CancellationToken ct = default);

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
    /// Applies owner edits to draft catalog items (spec 11 §5.2) within the ambient transaction.
    /// Each edit changes only its non-null fields; item ids are namespaced as
    /// <c>{catalogId}:{itemId}</c> to match the ingestion handler. Edits that match no row are no-ops.
    /// </summary>
    Task ApplyItemEditsAsync(string catalogId, IEnumerable<CatalogItemEdit> edits, CancellationToken ct = default);
}
