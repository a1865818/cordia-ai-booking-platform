using Cordia.Application.Handlers.Campaigns.CreateCampaign;
using FluentValidation.TestHelper;

namespace Cordia.Tests.UnitTests.Campaigns;

[Trait("Category", "Unit")]
public class CreateCampaignValidatorTests
{
    private readonly CreateCampaignValidator _validator = new();

    private static CreateCampaignCommand Valid() => new(
        "tenant-abc",
        "Win back no-shows",
        "customers with a no-show",
        "sms",
        "Hi {name}",
        8,
        "owner@example.com",
        "campaign_review",
        "owner");

    [Fact]
    public async Task Valid_command_passes()
    {
        var result = await _validator.TestValidateAsync(
            Valid(), cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Empty_title_fails()
    {
        var result = await _validator.TestValidateAsync(
            Valid() with { Title = "" }, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.Title);
    }

    [Fact]
    public async Task Unknown_channel_fails()
    {
        var result = await _validator.TestValidateAsync(
            Valid() with { Channel = "fax" }, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.Channel);
    }

    [Fact]
    public async Task Negative_recipients_fails()
    {
        var result = await _validator.TestValidateAsync(
            Valid() with { Recipients = -1 }, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.Recipients);
    }

    [Fact]
    public async Task Missing_actor_is_not_a_validation_error()
    {
        var result = await _validator.TestValidateAsync(
            Valid() with { Actor = "", ActorRole = "", ReasonCode = "" },
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotHaveAnyValidationErrors();
    }
}
