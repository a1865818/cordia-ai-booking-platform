using Cordia.Application.Handlers.DepositPolicy.SetDepositPolicy;
using FluentValidation.TestHelper;

namespace Cordia.Tests.UnitTests.DepositPolicy;

[Trait("Category", "Unit")]
public class SetDepositPolicyValidatorTests
{
    private readonly SetDepositPolicyValidator _validator = new();

    private static SetDepositPolicyCommand Valid(
        string amountPolicy = "per_service",
        long? amountMinor = null,
        string refundWindow = "24 hours")
        => new("tenant-1", true, amountPolicy, false, refundWindow, amountMinor, "AUD",
            "owner@example.com", "venue setup", "owner");

    [Fact]
    public async Task PerService_PassesWithoutAmountMinor()
    {
        var result = await _validator.TestValidateAsync(
            Valid(), cancellationToken: TestContext.Current.CancellationToken);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Flat_RequiresAmountMinor()
    {
        var result = await _validator.TestValidateAsync(
            Valid("flat"), cancellationToken: TestContext.Current.CancellationToken);
        result.ShouldHaveValidationErrorFor(x => x.AmountMinor);
    }

    [Fact]
    public async Task Flat_WithAmountMinor_Passes()
    {
        var result = await _validator.TestValidateAsync(
            Valid("flat", 5000), cancellationToken: TestContext.Current.CancellationToken);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task UnknownAmountPolicy_Fails()
    {
        var result = await _validator.TestValidateAsync(
            Valid("percent"), cancellationToken: TestContext.Current.CancellationToken);
        result.ShouldHaveValidationErrorFor(x => x.AmountPolicy);
    }

    [Fact]
    public async Task EmptyRefundWindow_Fails()
    {
        var result = await _validator.TestValidateAsync(
            Valid(refundWindow: ""), cancellationToken: TestContext.Current.CancellationToken);
        result.ShouldHaveValidationErrorFor(x => x.RefundWindow);
    }
}
