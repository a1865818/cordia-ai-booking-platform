using Cordia.Application.Handlers.Console.GetBookingOverview;
using Cordia.Application.Handlers.Console.ListBookingSchedule;
using Cordia.Application.Handlers.Console.ListConversationSessions;
using Cordia.Application.Handlers.Console.ListCustomers;
using Cordia.Application.Handlers.Console.ListOperations;
using Cordia.Application.Handlers.Console.ListWaitlistEnrollments;
using Cordia.Domain.Enums;

namespace Cordia.Tests.UnitTests.Console;

/// <summary>
/// Validators for the console read queries.
///
/// Because <c>SuppressImplicitRequiredAttributeForNonNullableReferenceTypes</c> is on,
/// FluentValidation is the sole enforcement — so every bound must be asserted here, not
/// assumed from the type. The bounds matter: an unbounded list query on a hot table is the
/// one way a read-only console screen could hurt the API.
/// </summary>
[Trait("Category", "Unit")]
public class ConsoleQueryValidatorTests
{
    private const string TenantId = "tenant-a";
    private static readonly DateTimeOffset From = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    // --- schedule ---------------------------------------------------------

    [Fact]
    public void Schedule_RejectsMissingTenant()
    {
        var result = new ListBookingScheduleValidator().Validate(
            new ListBookingScheduleQuery("", From, From.AddDays(7), 100));
        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ListBookingScheduleValidator.MaxLimit + 1)]
    public void Schedule_RejectsOutOfRangeLimit(int limit)
    {
        var result = new ListBookingScheduleValidator().Validate(
            new ListBookingScheduleQuery(TenantId, From, From.AddDays(7), limit));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Schedule_RejectsInvertedOrEmptyWindow()
    {
        var validator = new ListBookingScheduleValidator();
        Assert.False(validator.Validate(new ListBookingScheduleQuery(TenantId, From, From, 10)).IsValid);
        Assert.False(validator.Validate(new ListBookingScheduleQuery(TenantId, From, From.AddDays(-1), 10)).IsValid);
    }

    [Fact]
    public void Schedule_RejectsWindowWiderThanTheCap()
    {
        var result = new ListBookingScheduleValidator().Validate(
            new ListBookingScheduleQuery(
                TenantId,
                From,
                From + ListBookingScheduleValidator.MaxWindow + TimeSpan.FromDays(1),
                10));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Schedule_AcceptsAWellFormedRequest()
    {
        var result = new ListBookingScheduleValidator().Validate(
            new ListBookingScheduleQuery(TenantId, From, From.AddDays(7), ListBookingScheduleValidator.MaxLimit));
        Assert.True(result.IsValid);
    }

    // --- operations -------------------------------------------------------

    [Fact]
    public void Operations_RejectsOutOfRangeLimit()
    {
        var validator = new ListOperationsValidator();
        Assert.False(validator.Validate(new ListOperationsQuery(TenantId, [], 0)).IsValid);
        Assert.False(
            validator.Validate(new ListOperationsQuery(TenantId, [], ListOperationsValidator.MaxLimit + 1)).IsValid);
    }

    [Fact]
    public void Operations_AcceptsAnEmptyStatusFilterAsMeaningAll()
    {
        var result = new ListOperationsValidator().Validate(new ListOperationsQuery(TenantId, [], 50));
        Assert.True(result.IsValid);
    }

    [Fact]
    public void NeedsAttention_ContainsOnlyStatesThatWaitOnAPerson()
    {
        // Guards against silently adding a state that is still making progress on its own,
        // which would fill the operator's queue with noise.
        Assert.Equal(
            [
                OperationStatus.AwaitingUserAction,
                OperationStatus.AlternativesRequired,
                OperationStatus.Reconciling,
                OperationStatus.CompensationRequired,
                OperationStatus.ManualReview,
                OperationStatus.FailedFinal,
            ],
            ListOperationsQueryHandler.NeedsAttention);

        Assert.DoesNotContain(OperationStatus.Booked, ListOperationsQueryHandler.NeedsAttention);
        Assert.DoesNotContain(OperationStatus.Accepted, ListOperationsQueryHandler.NeedsAttention);
    }

    // --- sessions / waitlist / customers ----------------------------------

    [Fact]
    public void Sessions_RejectsOutOfRangeLimit()
    {
        var validator = new ListConversationSessionsValidator();
        Assert.False(validator.Validate(new ListConversationSessionsQuery(TenantId, 0)).IsValid);
        Assert.False(
            validator.Validate(
                new ListConversationSessionsQuery(TenantId, ListConversationSessionsValidator.MaxLimit + 1)).IsValid);
        Assert.True(
            validator.Validate(
                new ListConversationSessionsQuery(TenantId, ListConversationSessionsValidator.MaxLimit)).IsValid);
    }

    [Fact]
    public void Waitlist_RejectsOutOfRangeLimit()
    {
        var validator = new ListWaitlistEnrollmentsValidator();
        Assert.False(validator.Validate(new ListWaitlistEnrollmentsQuery(TenantId, true, 0)).IsValid);
        Assert.False(
            validator.Validate(
                new ListWaitlistEnrollmentsQuery(TenantId, true, ListWaitlistEnrollmentsValidator.MaxLimit + 1)).IsValid);
    }

    [Fact]
    public void Customers_RejectsOutOfRangeLimitAndOverlongQuery()
    {
        var validator = new ListCustomersValidator();
        Assert.False(validator.Validate(new ListCustomersQuery(TenantId, null, 0)).IsValid);
        Assert.False(
            validator.Validate(
                new ListCustomersQuery(TenantId, new string('x', ListCustomersValidator.MaxQueryLength + 1), 10)).IsValid);
        Assert.True(validator.Validate(new ListCustomersQuery(TenantId, null, 10)).IsValid);
        Assert.True(validator.Validate(new ListCustomersQuery(TenantId, "cust", 10)).IsValid);
    }

    // --- overview ---------------------------------------------------------

    [Fact]
    public void Overview_RejectsInvertedWindowAndOversizedWindow()
    {
        var validator = new GetBookingOverviewValidator();
        Assert.False(validator.Validate(new GetBookingOverviewQuery(TenantId, From, From)).IsValid);
        Assert.False(
            validator.Validate(
                new GetBookingOverviewQuery(
                    TenantId,
                    From,
                    From + GetBookingOverviewValidator.MaxWindow + TimeSpan.FromDays(1))).IsValid);
        Assert.True(validator.Validate(new GetBookingOverviewQuery(TenantId, From, From.AddDays(30))).IsValid);
    }
}
