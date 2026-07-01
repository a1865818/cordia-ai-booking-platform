using System.Text.Json;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;

namespace Cordia.Application.Services;

/// <summary>One stale pre-order line and the coarse machine reason it failed revalidation.</summary>
public sealed record StaleLine(string ItemId, string Reason)
{
    public const string ItemUnavailable = "item_unavailable";
    public const string PriceChanged = "price_changed";
    public const string ModifierUnavailable = "modifier_unavailable";
}

/// <summary>Outcome of revalidating a <see cref="PreorderReference"/> against the live published menu.</summary>
public sealed record RevalidationResult(bool IsOk, IReadOnlyList<StaleLine> StaleLines)
{
    public static RevalidationResult Ok() => new(true, []);
    public static RevalidationResult Stale(IReadOnlyList<StaleLine> lines) => new(false, lines);
}

/// <summary>
/// Re-checks a captured <see cref="PreorderReference"/> against the CURRENT published catalog before
/// the saga acquires anything (spec 11 §5.3, Inv-10). For each line it verifies the item is still
/// published, the captured per-unit price + currency still match, and every selected modifier is
/// still offered. Any mismatch makes the line stale → the saga must route to AlternativesRequired
/// rather than commit a pre-order at a stale price (no silent substitution, §21.2).
/// </summary>
public sealed class LiveItemRevalidationService(IVendorCatalogRepository catalog)
{
    public async Task<RevalidationResult> RevalidateAsync(
        PreorderReference reference, CancellationToken ct = default)
    {
        var stale = new List<StaleLine>();

        foreach (var line in reference.Lines ?? [])
        {
            var item = await catalog.GetPublishedItemAsync(line.ItemId, ct);
            if (item is null)
            {
                stale.Add(new StaleLine(line.ItemId, StaleLine.ItemUnavailable));
                continue;
            }

            if (item.PriceMinor != line.CapturedPriceAmountMinor
                || !string.Equals(item.Currency, line.CapturedPriceCurrency, StringComparison.Ordinal))
            {
                stale.Add(new StaleLine(line.ItemId, StaleLine.PriceChanged));
                continue;
            }

            if (line.ModifierIds is { Count: > 0 })
            {
                var available = ParseModifierIds(item.Modifiers);
                if (line.ModifierIds.Any(m => !available.Contains(m)))
                    stale.Add(new StaleLine(line.ItemId, StaleLine.ModifierUnavailable));
            }
        }

        return stale.Count == 0 ? RevalidationResult.Ok() : RevalidationResult.Stale(stale);
    }

    /// <summary>
    /// Extracts the set of <c>modifier_id</c> strings from an item's <c>modifiers</c> jsonb (a list
    /// of <c>CatalogModifier</c>). Returns an empty set for null/blank/malformed payloads — a line
    /// requesting a modifier the item no longer offers then reads as unavailable (fail-safe).
    /// </summary>
    private static HashSet<string> ParseModifierIds(string? modifiersJson)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(modifiersJson))
            return ids;

        try
        {
            using var doc = JsonDocument.Parse(modifiersJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return ids;

            foreach (var mod in doc.RootElement.EnumerateArray())
            {
                if (mod.ValueKind == JsonValueKind.Object
                    && mod.TryGetProperty("modifier_id", out var id)
                    && id.ValueKind == JsonValueKind.String)
                {
                    ids.Add(id.GetString()!);
                }
            }
        }
        catch (JsonException)
        {
            // Malformed jsonb — treat as no modifiers (fail-safe; the line will read stale).
        }

        return ids;
    }
}
