namespace Cordia.Domain.Menu;

/// <summary>
/// A draft catalog item produced by the isolated extraction agent (spec 11 §3).
/// Carries the draft-only extraction signals (confidence + source location) used for
/// owner-review flagging; these are NEVER surfaced to customers as fact (Inv-16).
/// </summary>
public sealed record DraftCatalogItem(
    string ItemId,
    string Name,
    string Description,
    long PriceMinor,
    string Currency,
    double? ExtractionConfidence,
    string? SourceLocation,
    string? Category = null,
    string? Modifiers = null,
    string? AvailabilityWindow = null,
    string? ImageRef = null);
