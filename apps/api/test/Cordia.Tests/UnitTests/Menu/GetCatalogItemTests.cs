using Cordia.Application.Handlers.Menu.GetCatalogItem;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.Menu;

/// <summary>
/// Unit tests for <see cref="GetCatalogItemQueryHandler"/> (spec 11 §7). Returns the
/// published item detail, or null when the repo finds no published item (→ 404 upstream).
/// </summary>
[Trait("Category", "Unit")]
public class GetCatalogItemTests
{
    private readonly Mock<IVendorCatalogRepository> _repo = new();

    private GetCatalogItemQueryHandler BuildHandler() => new(_repo.Object);

    [Fact]
    public async Task PublishedItemFound_MapsToDetail()
    {
        _repo.Setup(r => r.GetPublishedItemAsync("item_1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VendorCatalogItem
            {
                TenantId = "t1",
                ItemId = "item_1",
                CatalogId = "cat1",
                Name = "Grilled Salmon",
                Description = "Atlantic salmon",
                PriceMinor = 3200,
                Currency = "AUD",
                Category = "mains",
                Modifiers = """[{"modifier_id":"mod_1","name":"Extra cheese"}]""",
                AvailabilityWindow = """{"days_of_week":["fri"],"start_time":"17:00"}""",
                ImageRef = "s3://tenant/img/salmon.jpg",
                MenuMetadata = """{"category":"mains"}""",
                AllergenInfo = """{"contains":["fish"]}""",
            });

        var result = await BuildHandler().Handle(new GetCatalogItemQuery("item_1"), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("item_1", result!.ItemId);
        Assert.Equal(3200, result.PriceMinor);
        Assert.Equal("mains", result.Category);
        Assert.Equal("""[{"modifier_id":"mod_1","name":"Extra cheese"}]""", result.Modifiers);
        Assert.Equal("""{"days_of_week":["fri"],"start_time":"17:00"}""", result.AvailabilityWindow);
        Assert.Equal("s3://tenant/img/salmon.jpg", result.ImageRef);
        Assert.Equal("""{"category":"mains"}""", result.MenuMetadata);
        Assert.Equal("""{"contains":["fish"]}""", result.AllergenInfo);
    }

    [Fact]
    public async Task ItemNotFoundOrUnpublished_ReturnsNull()
    {
        _repo.Setup(r => r.GetPublishedItemAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((VendorCatalogItem?)null);

        var result = await BuildHandler().Handle(new GetCatalogItemQuery("missing"), CancellationToken.None);

        Assert.Null(result);
    }
}
