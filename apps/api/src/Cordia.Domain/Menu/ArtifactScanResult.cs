namespace Cordia.Domain.Menu;

/// <summary>Outcome of scanning an uploaded menu artifact before extraction (spec 11 §3, FM#4).</summary>
public enum ArtifactScanResult
{
    /// <summary>No malware detected; extraction may proceed.</summary>
    Clean,

    /// <summary>Malware detected; the artifact must be quarantined and never extracted.</summary>
    Detected,
}
