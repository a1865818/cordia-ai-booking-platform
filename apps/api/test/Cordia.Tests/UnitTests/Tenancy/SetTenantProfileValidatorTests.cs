using Cordia.Application.Handlers.Tenancy.SetTenantProfile;
using FluentValidation.TestHelper;

namespace Cordia.Tests.UnitTests.Tenancy;

[Trait("Category", "Unit")]
public class SetTenantProfileValidatorTests
{
    private readonly SetTenantProfileValidator _validator = new();

    private static SetTenantProfileCommand Valid(string? businessType = "salon", bool? waitlist = true)
        => new("tenant-1", businessType, waitlist, "owner@example.com", "venue setup", "owner");

    [Theory]
    [InlineData("salon")]
    [InlineData("restaurant")]
    [InlineData("medical")]
    [InlineData("fitness")]
    [InlineData("cleaning")]
    [InlineData("lawn")]
    [InlineData("repair")]
    [InlineData("other")]
    [InlineData("Salon")]
    public async Task AllowedBusinessType_Passes(string type)
    {
        var result = await _validator.TestValidateAsync(
            Valid(type), cancellationToken: TestContext.Current.CancellationToken);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task OmittedBusinessType_Passes()
    {
        var result = await _validator.TestValidateAsync(
            Valid(null), cancellationToken: TestContext.Current.CancellationToken);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task UnknownBusinessType_Fails()
    {
        var result = await _validator.TestValidateAsync(
            Valid("bakery"), cancellationToken: TestContext.Current.CancellationToken);
        result.ShouldHaveValidationErrorFor(x => x.BusinessType);
    }

    [Fact]
    public async Task EmptyTenantId_Fails()
    {
        var result = await _validator.TestValidateAsync(
            Valid() with { TenantId = "" }, cancellationToken: TestContext.Current.CancellationToken);
        result.ShouldHaveValidationErrorFor(x => x.TenantId);
    }
}
