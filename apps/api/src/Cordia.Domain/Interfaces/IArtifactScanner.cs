using Cordia.Domain.Menu;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Scans an uploaded menu artifact in object storage BEFORE any extraction runs
/// (spec 11 §3, FM#4). The caller quarantines on <see cref="ArtifactScanResult.Detected"/>.
/// The default implementation is a deterministic stub; real ClamAV/provider swaps in later.
/// </summary>
public interface IArtifactScanner
{
    /// <summary>Scans the artifact referenced by <paramref name="artifactRef"/> for malware.</summary>
    Task<ArtifactScanResult> ScanAsync(string artifactRef, CancellationToken ct = default);
}
