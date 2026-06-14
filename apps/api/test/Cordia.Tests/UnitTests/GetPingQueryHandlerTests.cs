using Cordia.Application.Ping;

namespace Cordia.Tests.UnitTests;

[Trait("Category", "Unit")]
public class GetPingQueryHandlerTests
{
    [Fact]
    public async Task Handle_ReturnsPongWithName()
    {
        var handler = new GetPingQueryHandler();

        var result = await handler.Handle(new GetPingQuery("Cordia"), CancellationToken.None);

        Assert.Equal("pong, Cordia", result.Message);
    }

    [Fact]
    public async Task Handle_DefaultsToWorld_WhenNameBlank()
    {
        var handler = new GetPingQueryHandler();

        var result = await handler.Handle(new GetPingQuery("  "), CancellationToken.None);

        Assert.Equal("pong, world", result.Message);
    }
}
