using Cordia.Application.Handlers.Menu.EditCatalogItems;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Menu;
using Moq;

namespace Cordia.Tests.UnitTests.Menu;

/// <summary>
/// Unit tests for <see cref="EditCatalogItemsCommandHandler"/> (spec 11 §5.2): edits draft items
/// while <c>in_review</c> (no status change), 404 on unknown, 409 when not in_review.
/// </summary>
[Trait("Category", "Unit")]
public class EditCatalogItemsTests
{
    private const string Tenant = "t-menu";
    private const string CatalogId = "cat1";

    private readonly Mock<IVendorCatalogRepository> _repo = new();
    private readonly Mock<IAuditEventRepository> _audit = new();
    private readonly Mock<IUnitOfWork> _uow = new();

    public EditCatalogItemsTests()
    {
        _audit.Setup(a => a.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _repo.Setup(r => r.ApplyItemEditsAsync(It.IsAny<string>(), It.IsAny<IEnumerable<CatalogItemEdit>>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _uow.Setup(u => u.ExecuteInTransactionAsync(It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((work, ct) => work(ct));
    }

    private EditCatalogItemsCommandHandler BuildHandler() => new(_repo.Object, _audit.Object, _uow.Object);

    private void SetupCatalog(CatalogStatus status)
        => _repo.Setup(r => r.GetAsync(CatalogId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VendorCatalog { TenantId = Tenant, CatalogId = CatalogId, Status = status });

    private static EditCatalogItemsCommand Command()
        => new(Tenant, CatalogId, "owner", new[] { new CatalogItemEdit("d1", "New name", null, 999) });

    [Fact]
    public async Task InReview_AppliesEdits_AndAudits_NoStatusChange()
    {
        SetupCatalog(CatalogStatus.InReview);

        await BuildHandler().Handle(Command(), CancellationToken.None);

        _repo.Verify(r => r.ApplyItemEditsAsync(CatalogId,
            It.Is<IEnumerable<CatalogItemEdit>>(e => e.Single().Name == "New name" && e.Single().PriceMinor == 999),
            It.IsAny<CancellationToken>()), Times.Once);
        _audit.Verify(a => a.AddAsync(It.Is<AuditEvent>(e => e.EventType == "catalog.items_edited"), It.IsAny<CancellationToken>()), Times.Once);
        // No transition methods are ever called by the edit handler.
        _repo.Verify(r => r.TryTransitionStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<CatalogStatus>(), It.IsAny<CatalogStatus>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnknownCatalog_ThrowsCatalogNotFound()
    {
        _repo.Setup(r => r.GetAsync(CatalogId, It.IsAny<CancellationToken>())).ReturnsAsync((VendorCatalog?)null);

        await Assert.ThrowsAsync<CatalogNotFoundException>(() => BuildHandler().Handle(Command(), CancellationToken.None));
    }

    [Theory]
    [InlineData(CatalogStatus.DraftReady)]
    [InlineData(CatalogStatus.Approved)]
    [InlineData(CatalogStatus.Published)]
    public async Task NotInReview_ThrowsCatalogNotEditable_AndAppliesNothing(CatalogStatus status)
    {
        SetupCatalog(status);

        await Assert.ThrowsAsync<CatalogNotEditableException>(
            () => BuildHandler().Handle(Command(), CancellationToken.None));

        _repo.Verify(r => r.ApplyItemEditsAsync(It.IsAny<string>(), It.IsAny<IEnumerable<CatalogItemEdit>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
