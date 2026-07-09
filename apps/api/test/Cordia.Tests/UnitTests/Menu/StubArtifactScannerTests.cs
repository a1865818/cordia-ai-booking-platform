using Cordia.Domain.Menu;
using Cordia.Infrastructure.Providers;

namespace Cordia.Tests.UnitTests.Menu;

/// <summary>Unit tests for the deterministic <see cref="StubArtifactScanner"/> (spec 11 §3, FM#4).</summary>
[Trait("Category", "Unit")]
public class StubArtifactScannerTests
{
    [Fact]
    public async Task CleanArtifact_ReturnsClean()
    {
        var result = await new StubArtifactScanner().ScanAsync("s3://tenant/menu-2026.pdf", CancellationToken.None);
        Assert.Equal(ArtifactScanResult.Clean, result);
    }

    [Theory]
    [InlineData("s3://tenant/malware.pdf")]
    [InlineData("s3://tenant/MALWARE-sample.bin")]
    public async Task MalwareSentinelArtifact_ReturnsDetected(string artifactRef)
    {
        var result = await new StubArtifactScanner().ScanAsync(artifactRef, CancellationToken.None);
        Assert.Equal(ArtifactScanResult.Detected, result);
    }

    [Fact]
    public async Task Scan_IsDeterministic()
    {
        var scanner = new StubArtifactScanner();
        var first = await scanner.ScanAsync("s3://tenant/menu.pdf", CancellationToken.None);
        var second = await scanner.ScanAsync("s3://tenant/menu.pdf", CancellationToken.None);
        Assert.Equal(first, second);
    }
}
