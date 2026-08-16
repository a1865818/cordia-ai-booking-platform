using Cordia.Application.Handlers.Tenancy.GetOperatingHours;
using Cordia.Application.Handlers.Tenancy.SetOperatingHours;
using FluentValidation.TestHelper;

namespace Cordia.Tests.UnitTests.Tenancy;

[Trait("Category", "Unit")]
public class SetOperatingHoursValidatorTests
{
    private readonly SetOperatingHoursValidator _validator = new();

    private static OperatingHoursDay Day(string d, string open = "09:00", string close = "18:00", bool on = true)
        => new(d, open, close, on);

    private static IReadOnlyList<OperatingHoursDay> SevenDays() =>
    [
        Day("Monday"), Day("Tuesday"), Day("Wednesday"), Day("Thursday"),
        Day("Friday"), Day("Saturday"), Day("Sunday", on: false),
    ];

    private static SetOperatingHoursCommand Command(IReadOnlyList<OperatingHoursDay> hours)
        => new("tenant-1", hours, "owner@example.com", "venue setup", "owner");

    [Fact]
    public async Task SevenUniqueDays_Passes()
    {
        var result = await _validator.TestValidateAsync(
            Command(SevenDays()), cancellationToken: TestContext.Current.CancellationToken);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task SixDays_Fails()
    {
        var result = await _validator.TestValidateAsync(
            Command(SevenDays().Take(6).ToList()),
            cancellationToken: TestContext.Current.CancellationToken);
        result.ShouldHaveValidationErrorFor(x => x.Hours);
    }

    [Fact]
    public async Task DuplicateDay_Fails()
    {
        var days = SevenDays().ToList();
        days[6] = Day("Monday");
        var result = await _validator.TestValidateAsync(
            Command(days), cancellationToken: TestContext.Current.CancellationToken);
        result.ShouldHaveValidationErrorFor(x => x.Hours);
    }

    [Fact]
    public async Task InvalidTime_Fails()
    {
        var days = SevenDays().ToList();
        days[0] = Day("Monday", open: "9:00 AM");
        var result = await _validator.TestValidateAsync(
            Command(days), cancellationToken: TestContext.Current.CancellationToken);
        result.ShouldHaveValidationErrorFor("Hours[0].Open");
    }
}
