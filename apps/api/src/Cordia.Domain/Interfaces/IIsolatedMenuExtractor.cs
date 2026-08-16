using Cordia.Domain.Menu;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Sandboxed menu-extraction seam (spec 11 §3): turns an uploaded artifact into DRAFT
/// catalog items plus advisory review flags. The port is data-in / data-out <b>by
/// construction</b> — it accepts only an opaque artifact reference and returns a plain
/// <see cref="MenuExtractionResult"/> (<see cref="DraftCatalogItem"/> +
/// <see cref="MenuExtractionFlag"/> records); no parameter or return type can carry a tool,
/// command, or capability, so a privileged action cannot be expressed across this boundary
/// (Inv-12 / §27.1). It can only produce drafts — it can never approve or publish a catalog.
///
/// <para>The real implementation is the LangGraph extraction-agent client, which calls the
/// isolated agent (empty tool allowlist, menu text carried as UNTRUSTED content) over a
/// dedicated RPC route. <c>StubIsolatedMenuExtractor</c> is <b>not</b> the default: it is an
/// explicit dev/test opt-in selected by configuration, kept so the suite and offline
/// development need no agent process.</para>
/// </summary>
public interface IIsolatedMenuExtractor
{
    /// <summary>
    /// Extracts draft catalog items — and any review flags — from the artifact at
    /// <paramref name="artifactRef"/>.
    /// </summary>
    /// <exception cref="Cordia.Domain.Exceptions.MenuExtractionFailedException">
    /// Extraction reached a definite failure. Implementations fail closed: a transport, model,
    /// or contract failure must throw, never return an empty result.
    /// </exception>
    Task<MenuExtractionResult> ExtractAsync(string artifactRef, CancellationToken ct = default);
}
