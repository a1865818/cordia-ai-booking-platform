namespace Cordia.Domain.Menu;

/// <summary>
/// A review signal the isolated extraction agent raises about the document it read
/// (spec 11 §27.6). Advisory only: a flag NEVER changes what the platform does — it is
/// surfaced to the owner, who decides. "Flag, never act."
///
/// <para><c>ItemId</c> is <c>null</c> for a document-level flag (one that is
/// about the artifact as a whole rather than a single extracted row).</para>
///
/// <para><b>The reason set is open by design.</b> The reasons the Python agent emits today
/// are <c>low_confidence</c>, <c>suspicious_content</c> and <c>rows_dropped</c>, but
/// <c>ExtractionFlag.reason</c> on the agent side is an unconstrained <c>str</c>, so .NET
/// must treat this as a free-form label: log it, audit it, show it — never switch on it
/// exhaustively, and never let an unrecognised reason change control flow.</para>
/// </summary>
public sealed record MenuExtractionFlag(string? ItemId, string Reason);
