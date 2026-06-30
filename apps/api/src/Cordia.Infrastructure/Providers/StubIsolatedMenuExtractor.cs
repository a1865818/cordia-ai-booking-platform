using Cordia.Domain.Interfaces;
using Cordia.Domain.Menu;

namespace Cordia.Infrastructure.Providers;

/// <summary>
/// Deterministic in-memory <see cref="IIsolatedMenuExtractor"/> used for development and as the
/// default implementation (spec 11 §3). Returns a fixed set of draft items regardless of input —
/// it never reads the artifact as instructions and exposes no privileged surface (the real
/// isolation boundary is enforced inside the LangGraph extraction agent). The real implementation
/// swaps in later behind the same port. Stateless → registered as a singleton.
/// </summary>
public sealed class StubIsolatedMenuExtractor : IIsolatedMenuExtractor
{
    public Task<IReadOnlyList<DraftCatalogItem>> ExtractAsync(string artifactRef, CancellationToken ct = default)
    {
        IReadOnlyList<DraftCatalogItem> items =
        [
            new("item_stub_1", "Sample Dish", "Deterministic stub extraction output", 1200, "AUD", 0.9, "stub:1"),
        ];
        return Task.FromResult(items);
    }
}
