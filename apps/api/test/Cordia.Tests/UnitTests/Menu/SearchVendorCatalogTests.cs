using Cordia.Application.Handlers.Menu.SearchVendorCatalog;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.Menu;

/// <summary>
/// Unit tests for <see cref="SearchVendorCatalogQueryHandler"/> (spec 11 §7). The repo is
/// mocked; the handler's job is to map published items to token-bounded summaries.
/// </summary>
[Trait("Category", "Unit")]
public class SearchVendorCatalogTests
{
    private readonly Mock<IVendorCatalogRepository> _repo = new();

    private SearchVendorCatalogQueryHandler BuildHandler() => new(_repo.Object);

    private static VendorCatalogItem Item(string id, string name) => new()
    {
        TenantId = "t1",
        ItemId = id,
        CatalogId = "cat1",
        Name = name,
        Description = $"{name} description",
        PriceMinor = 3200,
        Currency = "AUD",
        MenuMetadata = """{"category":"mains"}""",
        AllergenInfo = """{"contains":["fish"]}""",
    };

    [Fact]
    public async Task MapsRepoItemsToTokenBoundedSummaries()
    {
        _repo.Setup(r => r.SearchPublishedItemsAsync("salmon", 0, 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Item("item_1", "Grilled Salmon"), Item("item_2", "Salmon Soup")]);

        var result = await BuildHandler().Handle(new SearchVendorCatalogQuery("salmon", 0, 20), CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("item_1", result[0].ItemId);
        Assert.Equal("Grilled Salmon", result[0].Name);
        Assert.Equal(3200, result[0].PriceMinor);
        Assert.Equal("AUD", result[0].Currency);
    }

    [Fact]
    public async Task EmptyRepoResult_ReturnsEmptyList()
    {
        _repo.Setup(r => r.SearchPublishedItemsAsync(It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await BuildHandler().Handle(new SearchVendorCatalogQuery(null, 0, 20), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task PassesPagingAndQueryThroughToRepo()
    {
        _repo.Setup(r => r.SearchPublishedItemsAsync("x", 40, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await BuildHandler().Handle(new SearchVendorCatalogQuery("x", 40, 10), CancellationToken.None);

        _repo.Verify(r => r.SearchPublishedItemsAsync("x", 40, 10, It.IsAny<CancellationToken>()), Times.Once);
    }
}
