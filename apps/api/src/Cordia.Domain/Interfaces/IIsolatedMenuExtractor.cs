using Cordia.Domain.Menu;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Sandboxed menu-extraction seam (spec 11 §3): turns an uploaded artifact into DRAFT
/// catalog items. The port is data-in / data-out <b>by construction</b> — it accepts only an
/// opaque artifact reference and returns plain <see cref="DraftCatalogItem"/> records; no
/// parameter or return type can carry a tool, command, or capability, so a privileged action
/// cannot be expressed across this boundary (Inv-12 / §27.1). It can only produce drafts —
/// it can never approve or publish a catalog.
///
/// <para>The real implementation delegates to the isolated LangGraph extraction agent (which
/// enforces the empty-tool-surface + untrusted-text boundary); the default
/// <c>StubIsolatedMenuExtractor</c> is a deterministic seam.</para>
/// </summary>
public interface IIsolatedMenuExtractor
{
    /// <summary>Extracts draft catalog items from the artifact at <paramref name="artifactRef"/>.</summary>
    Task<IReadOnlyList<DraftCatalogItem>> ExtractAsync(string artifactRef, CancellationToken ct = default);
}
