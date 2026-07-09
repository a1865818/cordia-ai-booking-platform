using Cordia.Application.Common.Audit;
using Cordia.Application.Handlers.Menu.ReviewCatalog;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Menu;
using Moq;

namespace Cordia.Tests.UnitTests.Menu;

/// <summary>
/// Unit tests for <see cref="ReviewCatalogCommandHandler"/> (spec 11 §5.2): <c>draft_ready → in_review</c>
/// with optional item edits, 404 on unknown, 409 on illegal state / lost race.
/// </summary>
[Trait("Category", "Unit")]
public class ReviewCatalogTests
{
    private const string Tenant = "t-menu";
    private const string CatalogId = "cat1";

    private readonly Mock<IVendorCatalogRepository> _repo = new();
    private readonly Mock<IAuditEventRepository> _audit = new();
    private readonly Mock<IUnitOfWork> _uow = new();

    public ReviewCatalogTests()
    {
        _audit.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _repo.Setup(r => r.ApplyItemEditsAsync(It.IsAny<string>(), It.IsAny<IEnumerable<CatalogItemEdit>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _repo.Setup(r => r.TryTransitionStatusAsync(Tenant, CatalogId, It.IsAny<long>(),
            It.IsAny<CatalogStatus>(), It.IsAny<CatalogStatus>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        _uow.Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((work, ct) => work(ct));
    }

    private ReviewCatalogCommandHandler BuildHandler()
        => new(_repo.Object, new AuditWriter(_audit.Object), _uow.Object);

    private void SetupCatalog(CatalogStatus status, long version = 0)
        => _repo.Setup(r => r.GetAsync(CatalogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VendorCatalog { TenantId = Tenant, CatalogId = CatalogId, Status = status, Version = version });

    private static ReviewCatalogCommand Command(IReadOnlyList<CatalogItemEdit>? edits = null)
        => new(Tenant, CatalogId, "owner", edits ?? [], "rc_test");

    [Fact]
    public async Task DraftReady_TransitionsToInReview_AndAudits()
    {
        SetupCatalog(CatalogStatus.DraftReady);

        await BuildHandler().Handle(Command(), CancellationToken.None);

        _repo.Verify(r => r.TryTransitionStatusAsync(Tenant, CatalogId, 0,
            CatalogStatus.DraftReady, CatalogStatus.InReview, It.IsAny<CancellationToken>()), Times.Once);
        _audit.Verify(a => a.AddAsync(It.Is<AuditEvent>(e => e.EventType == "catalog.in_review"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Edits_AreApplied_WhenPresent()
    {
        SetupCatalog(CatalogStatus.DraftReady);
        var edits = new[] { new CatalogItemEdit("d1", "New name", null, 999) };

        await BuildHandler().Handle(Command(edits), CancellationToken.None);

        _repo.Verify(r => r.ApplyItemEditsAsync(CatalogId,
            It.Is<IEnumerable<CatalogItemEdit>>(e => e.Single().Name == "New name" && e.Single().PriceMinor == 999),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NoEdits_DoesNotCallApplyItemEdits()
    {
        SetupCatalog(CatalogStatus.DraftReady);

        await BuildHandler().Handle(Command(), CancellationToken.None);

        _repo.Verify(r => r.ApplyItemEditsAsync(It.IsAny<string>(), It.IsAny<IEnumerable<CatalogItemEdit>>(), It.IsAny<CancellationToken>()), Times.Never);
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
        SetupCatalog(CatalogStatus.Approved);

        await Assert.ThrowsAsync<InvalidCatalogStateTransitionException>(
            () => BuildHandler().Handle(Command(), CancellationToken.None));
    }

    [Fact]
    public async Task LostRace_ThrowsInvalidTransition()
    {
        SetupCatalog(CatalogStatus.DraftReady);
        _repo.Setup(r => r.TryTransitionStatusAsync(Tenant, CatalogId, It.IsAny<long>(),
            CatalogStatus.DraftReady, CatalogStatus.InReview, It.IsAny<CancellationToken>())).ReturnsAsync(0);

        await Assert.ThrowsAsync<InvalidCatalogStateTransitionException>(
            () => BuildHandler().Handle(Command(), CancellationToken.None));
    }
}
