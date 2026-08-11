namespace Cordia.Domain.Menu;

/// <summary>
/// Message types the menu-ingestion lane publishes (spec 11 §21).
///
/// <para>In Domain because both writers need them and neither can reference the other:
/// <c>IngestVendorMenuCommandHandler</c> lives in Application, <c>MenuIngestionMonitor</c> in
/// Infrastructure. Each previously declared its own copy of the same literal, with a comment
/// asserting the two were kept in step — which nothing enforced. Versioning the type
/// (<c>.v2</c>) would have changed one site, and an unregistered message type dead-letters, so
/// the drifted route's owner alerts would have stopped reaching anyone while both code paths
/// still looked correct.</para>
/// </summary>
public static class MenuMessageTypes
{
    /// <summary>Extraction reached a definite failure — the owner is told, and recovery is a fresh upload.</summary>
    public const string ExtractionFailed = "menu.catalog.extraction_failed.v1";
}
