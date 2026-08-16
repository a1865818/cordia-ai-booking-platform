using Cordia.Domain.Interfaces;
using Cordia.Domain.Menu;

namespace Cordia.Infrastructure.Providers;

/// <summary>
/// Deterministic in-memory <see cref="IIsolatedMenuExtractor"/> for offline development and the
/// test suite (spec 11 §3). It is <b>selected by configuration, not by default</b> — the real
/// LangGraph extraction-agent client is what runs unless the stub is explicitly switched on.
/// Returns a fixed set of draft items and no flags regardless of input — it never reads the
/// artifact as instructions and exposes no privileged surface (the real isolation boundary is
/// enforced inside the LangGraph extraction agent). Stateless → registered as a singleton.
/// </summary>
public sealed class StubIsolatedMenuExtractor : IIsolatedMenuExtractor
{
    public Task<MenuExtractionResult> ExtractAsync(string artifactRef, CancellationToken ct = default)
        => Task.FromResult(new MenuExtractionResult(
            [new DraftCatalogItem("item_stub_1", "Sample Dish", "Deterministic stub extraction output", 1200, "AUD", 0.9, "stub:1")],
            []));
}
