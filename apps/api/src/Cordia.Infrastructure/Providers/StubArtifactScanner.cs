using Cordia.Domain.Interfaces;
using Cordia.Domain.Menu;

namespace Cordia.Infrastructure.Providers;

/// <summary>
/// Deterministic in-memory <see cref="IArtifactScanner"/> used for development and as the
/// default implementation (spec 11 §3; real ClamAV/provider swaps in later). Any artifact
/// reference containing the <c>"malware"</c> sentinel is flagged <see cref="ArtifactScanResult.Detected"/>;
/// everything else is <see cref="ArtifactScanResult.Clean"/>. Stateless → registered as a singleton.
/// </summary>
public sealed class StubArtifactScanner : IArtifactScanner
{
    public Task<ArtifactScanResult> ScanAsync(string artifactRef, CancellationToken ct = default)
    {
        var result = artifactRef.Contains("malware", StringComparison.OrdinalIgnoreCase)
            ? ArtifactScanResult.Detected
            : ArtifactScanResult.Clean;
        return Task.FromResult(result);
    }
}
