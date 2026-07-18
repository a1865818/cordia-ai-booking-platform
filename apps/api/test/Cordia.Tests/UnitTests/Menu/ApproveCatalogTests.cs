using Cordia.Application.Common.Audit;
using Cordia.Application.Handlers.Menu.ApproveCatalog;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.Menu;

/// <summary>
/// Unit tests for <see cref="ApproveCatalogCommandHandler"/> (spec 11 §5.2): <c>in_review → approved</c>,
/// approver recorded, 404 on unknown, 409 on illegal state / lost race.
/// </summary>
[Trait("Category", "Unit")]
public class ApproveCatalogTests
{
    private const string Tenant = "t-menu";
    private const string CatalogId = "cat1";

    private readonly Mock<IVendorCatalogRepository> _repo = new();
    private readonly Mock<IAuditEventRepository> _audit = new();
    private readonly Mock<IUnitOfWork> _uow = new();

    public ApproveCatalogTests()
    {
        _audit.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _repo.Setup(r => r.TryApproveAsync(Tenant, CatalogId, It.IsAny<long>(),
            It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _uow.Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((work, ct) => work(ct));
    }

    private ApproveCatalogCommandHandler BuildHandler() => new(_repo.Object, new AuditWriter(_audit.Object), _uow.Object);

    private void SetupCatalog(CatalogStatus status, long version = 0)
        => _repo.Setup(r => r.GetAsync(CatalogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VendorCatalog { TenantId = Tenant, CatalogId = CatalogId, Status = status, Version = version });

    private static ApproveCatalogCommand Command() => new(Tenant, CatalogId, "alice", "rc_test", "owner");

    [Fact]
    public async Task InReview_Approves_RecordingApprover_AndAudits()
    {
        SetupCatalog(CatalogStatus.InReview, version: 3);

        await BuildHandler().Handle(Command(), CancellationToken.None);

        _repo.Verify(r => r.TryApproveAsync(Tenant, CatalogId, 3, "alice", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        _audit.Verify(a => a.AddAsync(It.Is<AuditEvent>(e => e.EventType == "catalog.approved" && e.Actor == "alice"), It.IsAny<CancellationToken>()), Times.Once);
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
        SetupCatalog(CatalogStatus.DraftReady);

        await Assert.ThrowsAsync<InvalidCatalogStateTransitionException>(
            () => BuildHandler().Handle(Command(), CancellationToken.None));
    }

    [Fact]
    public async Task LostRace_ThrowsInvalidTransition()
    {
        SetupCatalog(CatalogStatus.InReview);
        _repo.Setup(r => r.TryApproveAsync(Tenant, CatalogId, It.IsAny<long>(),
            It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);

        await Assert.ThrowsAsync<InvalidCatalogStateTransitionException>(
            () => BuildHandler().Handle(Command(), CancellationToken.None));
    }
}
