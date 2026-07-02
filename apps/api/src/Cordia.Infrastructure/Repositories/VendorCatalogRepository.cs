using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Menu;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.EntityFramework.Concurrency;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IVendorCatalogRepository"/>. Every query is
/// constrained to items whose catalog <see cref="CatalogStatus.Published"/> (Inv-16)
/// and is tenant-scoped by the global query filter (Inv-13). Reads use
/// <c>.AsNoTracking()</c>.
/// </summary>
public class VendorCatalogRepository(ApplicationDbContext db) : IVendorCatalogRepository
{
    public async Task<IReadOnlyList<VendorCatalogItem>> SearchPublishedItemsAsync(
        string? query, string? category, int skip, int take, CancellationToken ct = default)
    {
        var publishedCatalogIds = db.VendorCatalogs
            .Where(c => c.Status == CatalogStatus.Published)
            .Select(c => c.CatalogId);

        var items = db.VendorCatalogItems
            .AsNoTracking()
            .Where(i => publishedCatalogIds.Contains(i.CatalogId));

        if (!string.IsNullOrWhiteSpace(query))
        {
            // The customer's text must match LITERALLY: unescaped % / _ act as wildcards, and a
            // trailing backslash aborts the query with PG error 22025 (→ 500 on a foreground path).
            var pattern = $"%{SqlLikePattern.Escape(query)}%";
            items = items.Where(i =>
                EF.Functions.ILike(i.Name, pattern, SqlLikePattern.EscapeCharacter) ||
                EF.Functions.ILike(i.Description, pattern, SqlLikePattern.EscapeCharacter));
        }

        if (!string.IsNullOrWhiteSpace(category))
            items = items.Where(i => i.Category == category);

        return await items
            .OrderBy(i => i.Name)
            .ThenBy(i => i.ItemId)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);
    }

    public Task<VendorCatalogItem?> GetPublishedItemAsync(string itemId, CancellationToken ct = default)
    {
        var publishedCatalogIds = db.VendorCatalogs
            .Where(c => c.Status == CatalogStatus.Published)
            .Select(c => c.CatalogId);

        return db.VendorCatalogItems
            .AsNoTracking()
            .Where(i => i.ItemId == itemId && publishedCatalogIds.Contains(i.CatalogId))
            .FirstOrDefaultAsync(ct);
    }

    public Task<VendorCatalog?> GetAsync(string catalogId, CancellationToken ct = default)
        => db.VendorCatalogs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.CatalogId == catalogId, ct);

    public Task<int> TryTransitionStatusAsync(
        string tenantId,
        string catalogId,
        long expectedVersion,
        CatalogStatus expectedStatus,
        CatalogStatus next,
        CancellationToken ct = default)
        => VersionTokenUpdate.UpdateAsync<VendorCatalog>(
            db,
            expectedVersion,
            e => e.TenantId == tenantId && e.CatalogId == catalogId && e.Status == expectedStatus,
            s => s.SetProperty(x => x.Status, next),
            ct);

    public async Task AddCatalogAsync(VendorCatalog catalog, CancellationToken ct = default)
    {
        db.VendorCatalogs.Add(catalog);
        await db.SaveChangesAsync(ct);
    }

    public async Task AddDraftItemsAsync(IEnumerable<VendorCatalogItem> items, CancellationToken ct = default)
    {
        db.VendorCatalogItems.AddRange(items);
        await db.SaveChangesAsync(ct);
    }

    public Task<VendorCatalog?> GetCurrentPublishedAsync(string excludeCatalogId, CancellationToken ct = default)
        => db.VendorCatalogs
            .AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.Status == CatalogStatus.Published && c.CatalogId != excludeCatalogId, ct);

    public Task<int> TryApproveAsync(
        string tenantId,
        string catalogId,
        long expectedVersion,
        string approvedBy,
        DateTimeOffset approvedAt,
        CancellationToken ct = default)
        => VersionTokenUpdate.UpdateAsync<VendorCatalog>(
            db,
            expectedVersion,
            e => e.TenantId == tenantId && e.CatalogId == catalogId && e.Status == CatalogStatus.InReview,
            s => s
                .SetProperty(x => x.Status, CatalogStatus.Approved)
                .SetProperty(x => x.ApprovedBy, approvedBy)
                .SetProperty(x => x.ApprovedAt, approvedAt),
            ct);

    public async Task ApplyItemEditsAsync(
        string catalogId, IEnumerable<CatalogItemEdit> edits, CancellationToken ct = default)
    {
        foreach (var edit in edits)
        {
            // Symmetric namespacing (M5): the read path emits the full "{catalogId}:{rawId}" item id, so
            // the edit keys on exactly that id. Re-prefixing here would look for "{catalogId}:{catalogId}:…"
            // and silently match nothing. Data-layer immutability guard: only edit rows whose catalog is
            // still in_review (defense-in-depth on top of the handler's status pre-check). A miss is
            // surfaced (never a silent no-op) so a stale/mistyped id fails loudly.
            var affected = await db.VendorCatalogItems
                .Where(i => i.CatalogId == catalogId
                    && i.ItemId == edit.ItemId
                    && db.VendorCatalogs.Any(c => c.CatalogId == catalogId && c.Status == CatalogStatus.InReview))
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(x => x.Name, x => edit.Name ?? x.Name)
                        .SetProperty(x => x.Description, x => edit.Description ?? x.Description)
                        .SetProperty(x => x.PriceMinor, x => edit.PriceMinor ?? x.PriceMinor),
                    ct);

            if (affected == 0)
                throw new Cordia.Domain.Exceptions.CatalogItemNotFoundException(edit.ItemId);
        }
    }

    public Task ClearDraftExtractionMetadataAsync(string catalogId, CancellationToken ct = default)
        => db.VendorCatalogItems
            .Where(i => i.CatalogId == catalogId
                && (i.ExtractionConfidence != null || i.SourceLocation != null))
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.ExtractionConfidence, (double?)null)
                    .SetProperty(x => x.SourceLocation, (string?)null),
                ct);
}
