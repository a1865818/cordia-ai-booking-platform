using Cordia.Application.Handlers.Security.GetModelTrace;
using Cordia.Application.Handlers.Security.GetTranscript;
using Cordia.Domain.Enums;

namespace Cordia.Tests.UnitTests.Security;

/// <summary>
/// The ratified sensitive-read purposes (spec 14 §2 "role/purpose-restricted access", §3.1 step 4)
/// and their enforcement on the two audited PII reads.
///
/// The property under test is not "a reason was recorded" — the audit writer already did that
/// faithfully while every console read sent the same constant. It is that the recorded purpose can
/// *distinguish* one access from another.
/// </summary>
[Trait("Category", "Unit")]
public class SensitiveReadPurposeTests
{
    [Theory]
    [InlineData("dispute_review")]
    [InlineData("guest_complaint")]
    [InlineData("guest_request")]
    [InlineData("quality_review")]
    [InlineData("training_review")]
    [InlineData("security_investigation")]
    [InlineData("legal_hold")]
    public void RatifiedPurposesAreAccepted(string purpose)
        => Assert.True(SensitiveReadPurpose.IsRatified(purpose));

    [Theory]
    [InlineData("operator_review")]   // the old hard-coded console constant — deliberately NOT ratified
    [InlineData("just_looking")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void UnratifiedPurposesAreRejected(string? purpose)
        => Assert.False(SensitiveReadPurpose.IsRatified(purpose));

    [Fact]
    public void MatchingIsCaseSensitive()
    {
        // The audit column is grouped as an exact string. Admitting both spellings would split one
        // purpose across two rows in every report built on it.
        Assert.False(SensitiveReadPurpose.IsRatified("Dispute_Review"));
        Assert.False(SensitiveReadPurpose.IsRatified("DISPUTE_REVIEW"));
    }

    [Fact]
    public void AllIsTheCompleteSetAndEveryMemberIsRatified()
    {
        Assert.Equal(7, SensitiveReadPurpose.All.Count);
        Assert.All(SensitiveReadPurpose.All, p => Assert.True(SensitiveReadPurpose.IsRatified(p)));
        Assert.Equal(SensitiveReadPurpose.All.Count, SensitiveReadPurpose.All.Distinct().Count());
    }

    // ---------------------------------------------------------------------
    // Validator enforcement — transcript
    // ---------------------------------------------------------------------

    [Fact]
    public void TranscriptValidator_RejectsAnUnratifiedPurpose()
    {
        var result = new GetTranscriptValidator().Validate(
            new GetTranscriptQuery("t1", "sess_1", null, "op@venue", "operator", "just_looking"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(GetTranscriptQuery.ReasonCode));
    }

    [Fact]
    public void TranscriptValidator_AcceptsARatifiedPurpose()
    {
        var result = new GetTranscriptValidator().Validate(
            new GetTranscriptQuery("t1", "sess_1", null, "op@venue", "operator", SensitiveReadPurpose.DisputeReview));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void TranscriptValidator_LeavesAnAbsentPurposeToTheHandlersAuthorizationSpine()
    {
        // Absence must NOT become a 400 here: the handler answers it with a deny-AUDITED 403.
        // Failing structurally would skip the handler and lose that audit record.
        var result = new GetTranscriptValidator().Validate(
            new GetTranscriptQuery("t1", "sess_1", null, "op@venue", "operator", null));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ModelTraceValidator_LeavesReasonsFreeForm()
    {
        // Deliberate asymmetry. Trace reads are referenced to an incident ("incident_42"), not
        // drawn from a category list — ratifying a vocabulary here would replace a specific
        // pointer with a vague label. Only the transcript's purposes are categorical.
        var result = new GetModelTraceValidator().Validate(
            new GetModelTraceQuery("t1", "trace_1", "op@venue", "operator", "incident_42"));

        Assert.True(result.IsValid);
    }
}
