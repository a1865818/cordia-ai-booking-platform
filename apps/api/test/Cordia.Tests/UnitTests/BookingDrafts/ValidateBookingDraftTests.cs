using Cordia.Application.Handlers.BookingDrafts.ValidateBookingDraft;
using Cordia.Domain.Entities;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using FluentValidation.TestHelper;
using Moq;

namespace Cordia.Tests.UnitTests.BookingDrafts;

/// <summary>
/// Unit tests for <see cref="ValidateBookingDraftQueryHandler"/> and its validator
/// (spec 03 §4.3, §6.1).
///
/// The two cases that motivated the endpoint are here by name: a party of 400, and a table
/// at 3am. Both used to pass straight through to the availability lookup.
/// </summary>
[Trait("Category", "Unit")]
public class ValidateBookingDraftTests
{
    private const string TenantId = "t-validate-unit";
    private const string Zone = "Australia/Melbourne";

    /// <summary>Open 08:00 to 22:00 every day except Monday, like the seeded dev venue.</summary>
    private const string ClosedMondayHours = """
        [
            {"d":"Monday","open":"08:00","close":"22:00","on":false},
            {"d":"Tuesday","open":"08:00","close":"22:00","on":true},
            {"d":"Wednesday","open":"08:00","close":"22:00","on":true},
            {"d":"Thursday","open":"08:00","close":"22:00","on":true},
            {"d":"Friday","open":"08:00","close":"22:00","on":true},
            {"d":"Saturday","open":"08:00","close":"22:00","on":true},
            {"d":"Sunday","open":"08:00","close":"22:00","on":true}
        ]
        """;

    private readonly Mock<ITenantProfileRepository> _profiles = new();

    private ValidateBookingDraftQueryHandler Handler() => new(_profiles.Object);

    private void GivenProfile(string? timezone = Zone, string? hoursJson = ClosedMondayHours)
        => _profiles
            .Setup(p => p.GetAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TenantProfile { TenantId = TenantId, Timezone = timezone, HoursJson = hoursJson });

    /// <summary>
    /// A wall-clock time at the venue on the next given weekday after a fixed future date.
    ///
    /// Fixed rather than relative to today so the assertions never drift, and future so the
    /// "already been and gone" rule does not fire on every other case.
    /// </summary>
    private static DateTimeOffset AtVenue(DayOfWeek day, int hour, int minute = 0)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(Zone);
        var local = new DateTime(2030, 1, 1, hour, minute, 0, DateTimeKind.Unspecified);
        while (local.DayOfWeek != day)
            local = local.AddDays(1);
        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }

    private static BookingDraft Draft(DateTimeOffset? startsAt = null, long? partySize = null)
        => new()
        {
            TenantId = TenantId,
            SessionId = "s-1",
            SchemaVersion = "1.0.0",
            TenantPolicyVersion = "v7",
            Request = new BookingRequest
            {
                Operation = OperationTypeEnum.Create,
                ServiceId = "svc_dinner",
                LocationId = "loc_main",
                StartsAt = startsAt is null ? null : startsAt.Value,
                PartySize = partySize is null ? null : partySize.Value,
            },
        };

    private Task<ValidateBookingDraftResult> Validate(BookingDraft draft)
        => Handler().Handle(new ValidateBookingDraftQuery(TenantId, draft), TestContext.Current.CancellationToken);

    [Fact]
    public async Task PartyOf400_IsRejectedWithAHumanMessage()
    {
        GivenProfile();

        var result = await Validate(Draft(AtVenue(DayOfWeek.Tuesday, 19), partySize: 400));

        Assert.False(result.Validation.Valid);
        Assert.Equal(ValidateBookingDraftStatusEnum.PartySizeUnsupported, result.Status);
        Assert.Equal(["party_size"], result.Validation.RecoverableFields);
        Assert.Contains("big group", result.Validation.CustomerMessage);
        Assert.Equal("request_human_handoff", result.Validation.SuggestedAction);
    }

    [Fact]
    public async Task PartyAtTheLimit_IsAccepted()
    {
        GivenProfile();

        var result = await Validate(
            Draft(AtVenue(DayOfWeek.Tuesday, 19), partySize: ValidateBookingDraftQueryHandler.MaxPartySize));

        Assert.True(result.Validation.Valid);
        Assert.Equal(ValidateBookingDraftStatusEnum.Valid, result.Status);
    }

    [Fact]
    public async Task PartyOfZero_IsRejected()
    {
        GivenProfile();

        var result = await Validate(Draft(AtVenue(DayOfWeek.Tuesday, 19), partySize: 0));

        Assert.False(result.Validation.Valid);
        Assert.Equal(ValidateBookingDraftStatusEnum.PartySizeUnsupported, result.Status);
    }

    [Fact]
    public async Task ThreeInTheMorning_IsOutsideOperatingHours()
    {
        GivenProfile();

        var result = await Validate(Draft(AtVenue(DayOfWeek.Tuesday, 3), partySize: 2));

        Assert.False(result.Validation.Valid);
        Assert.Equal(ValidateBookingDraftStatusEnum.OutsideOperatingHours, result.Status);
        Assert.Equal(["starts_at"], result.Validation.RecoverableFields);
        Assert.Equal("request_alternative_time", result.Validation.SuggestedAction);
        Assert.Contains("8am till 10pm", result.Validation.CustomerMessage);
    }

    [Fact]
    public async Task ClosingTimeItself_IsOutsideOperatingHours()
    {
        GivenProfile();

        var result = await Validate(Draft(AtVenue(DayOfWeek.Tuesday, 22), partySize: 2));

        Assert.False(result.Validation.Valid);
        Assert.Equal(ValidateBookingDraftStatusEnum.OutsideOperatingHours, result.Status);
    }

    [Fact]
    public async Task AClosedDay_NamesTheDayAndTheDaysWeAreOpen()
    {
        GivenProfile();

        var result = await Validate(Draft(AtVenue(DayOfWeek.Monday, 19), partySize: 2));

        Assert.False(result.Validation.Valid);
        Assert.Equal(ValidateBookingDraftStatusEnum.OutsideOperatingHours, result.Status);
        Assert.Contains("closed Mondays", result.Validation.CustomerMessage);
        Assert.Contains("Tuesday to Sunday, 8am till 10pm", result.Validation.CustomerMessage);
    }

    [Fact]
    public async Task SevenInTheEveningOnAnOpenDay_IsValid()
    {
        GivenProfile();

        var result = await Validate(Draft(AtVenue(DayOfWeek.Tuesday, 19), partySize: 4));

        Assert.True(result.Validation.Valid);
        Assert.Equal("v7", result.Validation.PolicyVersion);
        Assert.Empty(result.Validation.RecoverableFields);
    }

    [Fact]
    public async Task ATimeThatHasPassed_IsRejected()
    {
        GivenProfile();

        var result = await Validate(Draft(new DateTimeOffset(2020, 6, 1, 19, 0, 0, TimeSpan.Zero), partySize: 2));

        Assert.False(result.Validation.Valid);
        Assert.Equal(ValidateBookingDraftStatusEnum.MinimumNoticeNotMet, result.Status);
        Assert.Equal(["starts_at"], result.Validation.RecoverableFields);
    }

    [Fact]
    public async Task WithoutATimezone_TheHoursRuleIsSkippedRatherThanGuessed()
    {
        // Evaluating a wall clock in UTC for a venue ten hours away would reject the dinner
        // sitting and accept 5am, so a missing zone must not silently become one.
        GivenProfile(timezone: null);

        var result = await Validate(Draft(AtVenue(DayOfWeek.Tuesday, 3), partySize: 2));

        Assert.True(result.Validation.Valid);
    }

    [Fact]
    public async Task WithNoProfileRow_TheDraftIsNotBlocked()
    {
        _profiles
            .Setup(p => p.GetAsync(TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TenantProfile?)null);

        var result = await Validate(Draft(AtVenue(DayOfWeek.Tuesday, 19), partySize: 2));

        Assert.True(result.Validation.Valid);
    }

    [Fact]
    public async Task AnIncompleteDraft_IsNotAPolicyFailure()
    {
        // Missing fields belong to the capture loop: answering "how many of you?" with a
        // policy message would talk over the clarification the agent was about to make.
        GivenProfile();

        var result = await Validate(Draft());

        Assert.True(result.Validation.Valid);
        Assert.Equal(ValidateBookingDraftStatusEnum.Valid, result.Status);
    }

    [Fact]
    public async Task ADraftWithoutAPolicyVersion_ReportsItAsUnknown()
    {
        GivenProfile();
        var draft = Draft(AtVenue(DayOfWeek.Tuesday, 19), partySize: 2);
        draft.TenantPolicyVersion = "";

        var result = await Validate(draft);

        Assert.Equal(ValidateBookingDraftQueryHandler.UnknownPolicyVersion, result.Validation.PolicyVersion);
    }

    [Fact]
    public async Task Validator_RequiresATenantAndADraftRequest()
    {
        var validator = new ValidateBookingDraftValidator();

        var missingTenant = await validator.TestValidateAsync(
            new ValidateBookingDraftQuery("", Draft()), cancellationToken: TestContext.Current.CancellationToken);
        missingTenant.ShouldHaveValidationErrorFor(x => x.TenantId);

        var missingRequest = await validator.TestValidateAsync(
            new ValidateBookingDraftQuery(TenantId, new BookingDraft()),
            cancellationToken: TestContext.Current.CancellationToken);
        missingRequest.ShouldHaveValidationErrorFor(x => x.Draft.Request);
    }
}
