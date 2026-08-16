using Cordia.Application.Handlers.Campaigns.ScheduleCampaign;
using FluentValidation.TestHelper;

namespace Cordia.Tests.UnitTests.Campaigns;

[Trait("Category", "Unit")]
public class ScheduleCampaignValidatorTests
{
    private readonly ScheduleCampaignValidator _validator = new();

    private static ScheduleCampaignCommand Valid() => new(
        "tenant-abc",
        "cp_1",
        new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero),
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
    public async Task Missing_send_at_fails()
    {
        var result = await _validator.TestValidateAsync(
            Valid() with { SendAt = null }, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.SendAt);
    }

    [Fact]
    public async Task Empty_campaign_id_fails()
    {
        var result = await _validator.TestValidateAsync(
            Valid() with { CampaignId = "" }, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.CampaignId);
    }
}
