namespace Cordia.Domain.Menu;

/// <summary>
/// Everything the isolated extraction agent is allowed to hand back: the draft items it read
/// and the review flags it raised. This <c>items</c> + <c>flags</c> pair is the sanctioned
/// extraction contract (spec 11 §27.6 / Inv-12) and mirrors the agent's own
/// <c>ExtractionResult</c> one-for-one.
///
/// <para><b>No field here may ever express a tool call, command, or capability</b>, and this
/// record must not be widened. The isolation property of the extraction seam is structural:
/// the return type physically cannot carry a privileged action, so untrusted document content
/// cannot become one no matter what the model emits. Adding a field that names an action, a
/// callback, a URL to fetch, or a status to set would dissolve that property — flags are
/// advisory data for the owner, nothing more.</para>
/// </summary>
public sealed record MenuExtractionResult(
    IReadOnlyList<DraftCatalogItem> Items,
    IReadOnlyList<MenuExtractionFlag> Flags);
