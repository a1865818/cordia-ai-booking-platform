using Cordia.Application.Handlers.BookingOperations.SubmitConfirmedIntent;
using Cordia.Application.Handlers.Waitlist;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.Services;

namespace Cordia.Tests.UnitTests.Waitlist;

/// <summary>
/// Regression guard for spec 11 §5.1 (divergence #1): a waitlist accept is minted through the spec-04
/// front door, so the <see cref="SubmitConfirmedIntentCommand"/> that
/// <see cref="WaitlistAcceptSubmission.Build"/> produces MUST satisfy the REAL
/// <see cref="SubmitConfirmedIntentValidator"/> — the same structural gate the MediatR
/// <c>ValidationPipelineBehavior</c> runs on every <c>mediator.Send</c>.
///
/// <para>The handler unit tests mock <c>IMediator</c>, so a submission that omits a validator-required
/// evidence field (schema/policy/read-back/source-turn version) passes there but would be rejected 400
/// at runtime before ever reaching spec 04. This test drives the actual validator so that gap is
/// covered without standing up the full 04→06 stack.</para>
/// </summary>
[Trait("Category", "Unit")]
public class WaitlistAcceptSubmissionValidationTests
{
    private const string TenantId = "t_1";
    private static readonly CanonicalRequestHasher Hasher = new();
    private static readonly SubmitConfirmedIntentValidator Validator = new();

    [Fact]
    public void CustomerAccept_Submission_Satisfies_Spec04_Validator()
    {
        var cmd = WaitlistAcceptSubmission.Build(
            TenantId, SampleOffer(), SampleEnrollment(), Hasher, DateTimeOffset.UtcNow);

        var result = Validator.Validate(cmd);

        Assert.True(result.IsValid,
            "WaitlistAcceptSubmission must satisfy the spec-04 structural validator; failures: " +
            string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void AutoBook_Submission_With_ConsentEvidence_Satisfies_Spec04_Validator()
    {
        var cmd = WaitlistAcceptSubmission.Build(
            TenantId, SampleOffer(), SampleEnrollment(), Hasher, DateTimeOffset.UtcNow,
            autoBookConsentEvidenceId: "consent_1");

        var result = Validator.Validate(cmd);

        Assert.True(result.IsValid,
            "Auto-book WaitlistAcceptSubmission must satisfy the spec-04 structural validator; failures: " +
            string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    private static WaitlistOffer SampleOffer() => new()
    {
        TenantId = TenantId,
        OfferId = "wlo_1",
        EnrollmentId = "wle_1",
        OperationId = "op_wl_1",
        HoldId = "hold_1",
        SlotId = "slot_1",
        SlotStartAt = DateTimeOffset.UtcNow.AddDays(1),
        AvailableSeats = 2,
        Status = WaitlistOfferStatus.Offered,
        Version = 1,
    };

    private static WaitlistEnrollment SampleEnrollment() => new()
    {
        TenantId = TenantId,
        EnrollmentId = "wle_1",
        CustomerRef = "cust_1",
        ServiceId = "svc_1",
        LocationId = "loc_1",
        PartySize = 2,
        NotificationChannel = "email",
        IdentityRef = "id_cust_1",
        VerificationLevel = "channel_verified",
    };
}
