using Cordia.Application.Handlers.Menu.IngestVendorMenu;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Menu;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Cordia.Tests.UnitTests.Menu;

/// <summary>
/// Unit tests for <see cref="IngestVendorMenuCommandHandler"/> (spec 11 §5.2). All deps mocked;
/// the handler walks the catalog state machine and fails securely (never throws on expected failure).
/// </summary>
[Trait("Category", "Unit")]
public class IngestVendorMenuTests
{
    private const string Tenant = "t-menu";
    private const string CatalogId = "cat1";

    private readonly Mock<IVendorCatalogRepository> _repo = new();
    private readonly Mock<IArtifactScanner> _scanner = new();
    private readonly Mock<IIsolatedMenuExtractor> _extractor = new();
    private readonly Mock<IAuditEventRepository> _audit = new();
    private readonly Mock<IOutboxRepository> _outbox = new();

    private IngestVendorMenuCommandHandler BuildHandler()
        => new(_repo.Object, _scanner.Object, _extractor.Object, _audit.Object, _outbox.Object,
            NullLogger<IngestVendorMenuCommandHandler>.Instance);

    private static IngestVendorMenuCommand Command()
        => new(Tenant, CatalogId, "s3://tenant/menu.pdf", "msg-1", "corr-1");

    public IngestVendorMenuTests()
    {
        _audit.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _outbox.Setup(o => o.AddAsync(It.IsAny<OutboxMessage>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _repo.Setup(r => r.AddDraftItemsAsync(It.IsAny<IEnumerable<VendorCatalogItem>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        // All guarded transitions succeed unless a test overrides.
        _repo.Setup(r => r.TryTransitionStatusAsync(Tenant, CatalogId, It.IsAny<long>(),
            It.IsAny<CatalogStatus>(), It.IsAny<CatalogStatus>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
    }

    private void SetupCatalog(CatalogStatus status, string tenantId = Tenant, long version = 0)
        => _repo.Setup(r => r.GetAsync(CatalogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VendorCatalog { TenantId = tenantId, CatalogId = CatalogId, Status = status, Version = version });

    [Fact]
    public async Task CleanScan_ExtractsAndTransitionsToDraftReady()
    {
        SetupCatalog(CatalogStatus.Uploaded);
        _scanner.Setup(s => s.ScanAsync("s3://tenant/menu.pdf", It.IsAny<CancellationToken>())).ReturnsAsync(ArtifactScanResult.Clean);
        _extractor.Setup(e => e.ExtractAsync("s3://tenant/menu.pdf", It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DraftCatalogItem("d1", "Soup", "Hot soup", 1200, "AUD", 0.85, "page_1")]);

        await BuildHandler().Handle(Command(), CancellationToken.None);

        // Version threading: 0 → Scanning, 1 → Extracting, 2 → DraftReady.
        _repo.Verify(r => r.TryTransitionStatusAsync(Tenant, CatalogId, 0, CatalogStatus.Uploaded, CatalogStatus.Scanning, It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(r => r.TryTransitionStatusAsync(Tenant, CatalogId, 1, CatalogStatus.Scanning, CatalogStatus.Extracting, It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(r => r.TryTransitionStatusAsync(Tenant, CatalogId, 2, CatalogStatus.Extracting, CatalogStatus.DraftReady, It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(r => r.AddDraftItemsAsync(
            It.Is<IEnumerable<VendorCatalogItem>>(items =>
                items.Count() == 1 &&
                items.First().ItemId == "cat1:d1" &&
                items.First().ExtractionConfidence == 0.85),
            It.IsAny<CancellationToken>()), Times.Once);
        _audit.Verify(a => a.AddAsync(It.Is<AuditEvent>(e => e.EventType == "catalog.draft_ready"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MalwareScan_Quarantines_AndNeverExtracts()
    {
        SetupCatalog(CatalogStatus.Uploaded);
        _scanner.Setup(s => s.ScanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(ArtifactScanResult.Detected);

        await BuildHandler().Handle(Command(), CancellationToken.None);

        _repo.Verify(r => r.TryTransitionStatusAsync(Tenant, CatalogId, 1, CatalogStatus.Scanning, CatalogStatus.Quarantined, It.IsAny<CancellationToken>()), Times.Once);
        _extractor.Verify(e => e.ExtractAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _repo.Verify(r => r.AddDraftItemsAsync(It.IsAny<IEnumerable<VendorCatalogItem>>(), It.IsAny<CancellationToken>()), Times.Never);
        _audit.Verify(a => a.AddAsync(It.Is<AuditEvent>(e => e.EventType == "catalog.quarantined"), It.IsAny<CancellationToken>()), Times.Once);
        // The owner is alerted (Inv-14): a quarantine event is enqueued on the outbox carrying only the catalog id.
        _outbox.Verify(o => o.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == IngestVendorMenuCommandHandler.QuarantinedEventType
                && m.CorrelationId == "corr-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CatalogNotFound_AckAndDrop()
    {
        _repo.Setup(r => r.GetAsync(CatalogId, It.IsAny<CancellationToken>())).ReturnsAsync((VendorCatalog?)null);

        await BuildHandler().Handle(Command(), CancellationToken.None);

        _repo.Verify(r => r.TryTransitionStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<CatalogStatus>(), It.IsAny<CatalogStatus>(), It.IsAny<CancellationToken>()), Times.Never);
        _scanner.Verify(s => s.ScanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WrongTenant_AckAndDrop()
    {
        SetupCatalog(CatalogStatus.Uploaded, tenantId: "other-tenant");

        await BuildHandler().Handle(Command(), CancellationToken.None);

        _scanner.Verify(s => s.ScanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnexpectedStatus_AckAndDrop()
    {
        SetupCatalog(CatalogStatus.DraftReady);

        await BuildHandler().Handle(Command(), CancellationToken.None);

        _repo.Verify(r => r.TryTransitionStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<CatalogStatus>(), It.IsAny<CatalogStatus>(), It.IsAny<CancellationToken>()), Times.Never);
        _scanner.Verify(s => s.ScanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A detected extraction failure is settled HERE, not left for the staleness sweeper.
    ///
    /// <para>This handler already knows the extraction failed. Leaving the row in
    /// <c>Extracting</c> for <c>MenuIngestionMonitor</c> to infer from a threshold meant the
    /// console reported "being read" for up to StalenessSeconds after the platform had a
    /// definite answer. The monitor remains the backstop for a worker that died before reaching
    /// this branch at all.</para>
    /// </summary>
    [Fact]
    public async Task ExtractionThrows_SettlesExtractFailedImmediately_NoThrow()
    {
        SetupCatalog(CatalogStatus.Uploaded);
        _scanner.Setup(s => s.ScanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(ArtifactScanResult.Clean);
        _extractor.Setup(e => e.ExtractAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("artifact unreadable"));

        // Must NOT throw (fail-secure inside the inbox txn).
        await BuildHandler().Handle(Command(), CancellationToken.None);

        _audit.Verify(a => a.AddAsync(It.Is<AuditEvent>(e => e.EventType == "catalog.extraction_failed"), It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(r => r.AddDraftItemsAsync(It.IsAny<IEnumerable<VendorCatalogItem>>(), It.IsAny<CancellationToken>()), Times.Never);
        _repo.Verify(r => r.TryTransitionStatusAsync(Tenant, CatalogId, It.IsAny<long>(),
            CatalogStatus.Extracting, CatalogStatus.DraftReady, It.IsAny<CancellationToken>()), Times.Never);
        // Settled terminally, at the version the Scanning→Extracting transition left behind.
        _repo.Verify(r => r.TryTransitionStatusAsync(Tenant, CatalogId, 2,
            CatalogStatus.Extracting, CatalogStatus.ExtractFailed, It.IsAny<CancellationToken>()), Times.Once);
        // The owner is alerted (Inv-14): an extraction-failed event is enqueued on the outbox.
        // A terminal status the owner never looks at is not a notification.
        _outbox.Verify(o => o.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == IngestVendorMenuCommandHandler.ExtractionFailedEventType),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Losing the settle race is not an error: the monitor or a concurrent writer already moved
    /// the row, and that writer wins. The handler must still not throw — a throw here rolls back
    /// the inbox admission and the message returns as poison (Inv-5).
    /// </summary>
    [Fact]
    public async Task ExtractionThrows_LostSettleRace_StillDoesNotThrow()
    {
        SetupCatalog(CatalogStatus.Uploaded);
        _scanner.Setup(s => s.ScanAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(ArtifactScanResult.Clean);
        _extractor.Setup(e => e.ExtractAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("artifact unreadable"));
        _repo.Setup(r => r.TryTransitionStatusAsync(Tenant, CatalogId, It.IsAny<long>(),
                CatalogStatus.Extracting, CatalogStatus.ExtractFailed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await BuildHandler().Handle(Command(), CancellationToken.None);

        // The failure is still recorded and still surfaced, whoever won the status write.
        _audit.Verify(a => a.AddAsync(It.Is<AuditEvent>(e => e.EventType == "catalog.extraction_failed"), It.IsAny<CancellationToken>()), Times.Once);
        _outbox.Verify(o => o.AddAsync(
            It.Is<OutboxMessage>(m => m.MessageType == IngestVendorMenuCommandHandler.ExtractionFailedEventType),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Command_IsInboxConsumerRequest_ForMenuProcessor()
    {
        // Wires the command into the inbox dedup pipeline (Inv-5) under the spec-11 consumer name.
        var cmd = Command();
        Assert.Equal("menu.processor", cmd.ConsumerName);
    }
}
