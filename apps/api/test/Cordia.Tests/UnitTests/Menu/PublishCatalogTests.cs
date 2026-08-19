using Cordia.Application.Common.Audit;
using Cordia.Application.Handlers.Menu.PublishCatalog;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.Menu;

/// <summary>
/// Unit tests for <see cref="PublishCatalogCommandHandler"/> (spec 11 §5.2): <c>approved → published</c>,
/// supersedes the prior live version, 404 on unknown, 409 on illegal state / lost race / empty catalog.
/// </summary>
[Trait("Category", "Unit")]
public class PublishCatalogTests
{
    private const string Tenant = "t-menu";
    private const string CatalogId = "cat2";
    private const string PriorId = "cat1";

    private readonly Mock<IVendorCatalogRepository> _repo = new();
    private readonly Mock<IAuditEventRepository> _audit = new();
    private readonly Mock<IUnitOfWork> _uow = new();

    public PublishCatalogTests()
    {
        _audit.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _repo.Setup(r => r.TryTransitionStatusAsync(Tenant, It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<CatalogStatus>(), It.IsAny<CatalogStatus>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        // No prior published by default.
        _repo.Setup(r => r.GetCurrentPublishedAsync(CatalogId, It.IsAny<CancellationToken>())).ReturnsAsync((VendorCatalog?)null);
        // A publishable catalog has at least one item by default (the empty case is its own test).
        SetupDraftItems(new VendorCatalogItem
        {
            TenantId = Tenant,
            CatalogId = CatalogId,
            ItemId = $"{CatalogId}:d1",
            Name = "Dish",
            PriceMinor = 1000,
            Currency = "AUD",
        });
        _uow.Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((work, ct) => work(ct));
    }

    private PublishCatalogCommandHandler BuildHandler() => new(_repo.Object, new AuditWriter(_audit.Object), _uow.Object);

    private void SetupCatalog(CatalogStatus status, long version = 0)
        => _repo.Setup(r => r.GetAsync(CatalogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VendorCatalog { TenantId = Tenant, CatalogId = CatalogId, Status = status, Version = version, VersionLabel = "menu_v2" });

    private void SetupDraftItems(params VendorCatalogItem[] items)
        => _repo.Setup(r => r.GetDraftItemsAsync(CatalogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);

    private static PublishCatalogCommand Command() => new(Tenant, CatalogId, "owner", "rc_test", "owner");

    [Fact]
    public async Task Approved_NoPrior_Publishes_AndAudits()
    {
        SetupCatalog(CatalogStatus.Approved, version: 5);

        await BuildHandler().Handle(Command(), CancellationToken.None);

        _repo.Verify(r => r.TryTransitionStatusAsync(Tenant, CatalogId, 5,
            CatalogStatus.Approved, CatalogStatus.Published, It.IsAny<CancellationToken>()), Times.Once);
        _audit.Verify(a => a.AddAsync(It.Is<AuditEvent>(e => e.EventType == "catalog.published"), It.IsAny<CancellationToken>()), Times.Once);
        _audit.Verify(a => a.AddAsync(It.Is<AuditEvent>(e => e.EventType == "catalog.superseded"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Approved_WithPrior_SupersedesPrior_ThenPublishes()
    {
        SetupCatalog(CatalogStatus.Approved, version: 5);
        _repo.Setup(r => r.GetCurrentPublishedAsync(CatalogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VendorCatalog { TenantId = Tenant, CatalogId = PriorId, Status = CatalogStatus.Published, Version = 9 });

        await BuildHandler().Handle(Command(), CancellationToken.None);

        // Prior superseded with its own version guard.
        _repo.Verify(r => r.TryTransitionStatusAsync(Tenant, PriorId, 9,
            CatalogStatus.Published, CatalogStatus.Superseded, It.IsAny<CancellationToken>()), Times.Once);
        // This catalog published.
        _repo.Verify(r => r.TryTransitionStatusAsync(Tenant, CatalogId, 5,
            CatalogStatus.Approved, CatalogStatus.Published, It.IsAny<CancellationToken>()), Times.Once);
        _audit.Verify(a => a.AddAsync(It.Is<AuditEvent>(e => e.EventType == "catalog.superseded" && e.SubjectRef == PriorId), It.IsAny<CancellationToken>()), Times.Once);
        _audit.Verify(a => a.AddAsync(It.Is<AuditEvent>(e => e.EventType == "catalog.published"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnknownCatalog_ThrowsCatalogNotFound()
    {
        _repo.Setup(r => r.GetAsync(CatalogId, It.IsAny<CancellationToken>())).ReturnsAsync((VendorCatalog?)null);

        await Assert.ThrowsAsync<CatalogNotFoundException>(() => BuildHandler().Handle(Command(), CancellationToken.None));
    }

    [Fact]
    public async Task WrongStatus_ThrowsInvalidTransition()
    {
        SetupCatalog(CatalogStatus.InReview);

        await Assert.ThrowsAsync<InvalidCatalogStateTransitionException>(
            () => BuildHandler().Handle(Command(), CancellationToken.None));
    }

    [Fact]
    public async Task EmptyCatalog_ThrowsCatalogHasNoItems_AndLeavesPriorPublished()
    {
        // Issue #89: an approved catalog with zero items must not publish. The assertion that
        // matters is the SECOND one — the prior live version is never touched, so the tenant's
        // menu survives a refused publish (no partial-effect window).
        SetupCatalog(CatalogStatus.Approved, version: 5);
        SetupDraftItems(); // extraction legitimately produced nothing
        _repo.Setup(r => r.GetCurrentPublishedAsync(CatalogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VendorCatalog { TenantId = Tenant, CatalogId = PriorId, Status = CatalogStatus.Published, Version = 9 });

        var ex = await Assert.ThrowsAsync<CatalogHasNoItemsException>(
            () => BuildHandler().Handle(Command(), CancellationToken.None));
        Assert.Equal(CatalogId, ex.CatalogId);

        // Nothing transitioned: the prior version was NOT superseded and this one was NOT published.
        _repo.Verify(r => r.TryTransitionStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<CatalogStatus>(), It.IsAny<CatalogStatus>(), It.IsAny<CancellationToken>()), Times.Never);
        _audit.Verify(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LostRaceOnPublish_ThrowsInvalidTransition()
    {
        SetupCatalog(CatalogStatus.Approved);
        _repo.Setup(r => r.TryTransitionStatusAsync(Tenant, CatalogId, It.IsAny<long>(),
            CatalogStatus.Approved, CatalogStatus.Published, It.IsAny<CancellationToken>())).ReturnsAsync(0);

        await Assert.ThrowsAsync<InvalidCatalogStateTransitionException>(
            () => BuildHandler().Handle(Command(), CancellationToken.None));
    }
}
