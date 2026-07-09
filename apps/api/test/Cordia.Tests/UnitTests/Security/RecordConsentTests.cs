using Cordia.Application.Common.Audit;
using Cordia.Application.Handlers.Security.RecordConsent;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Moq;
using DomainConsentState = Cordia.Domain.Enums.ConsentState;

namespace Cordia.Tests.UnitTests.Security;

[Trait("Category", "Unit")]
public class RecordConsentTests
{
    private const string TenantId = "tenant-consent";
    private const string SessionId = "sess-1";

    private readonly Mock<IConversationSessionRepository> _sessions = new();
    private readonly Mock<IAuditWriter> _audit = new();
    private readonly Mock<IUnitOfWork> _uow = new();

    public RecordConsentTests()
    {
        _uow.Setup(u => u.ExecuteInTransactionAsync(
                It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<CancellationToken, Task> work, CancellationToken ct) => work(ct));
    }

    private RecordConsentCommandHandler BuildHandler()
        => new(_sessions.Object, _audit.Object, _uow.Object);

    private static RecordConsentCommand MakeCommand(
        string outcome = "granted", bool recordingAllowed = true)
        => new(
            TenantId,
            SessionId,
            outcome,
            "us-ca-two-party:2026-01",
            "jp_us_ca",
            recordingAllowed,
            new DateTimeOffset(2026, 7, 9, 10, 0, 0, TimeSpan.Zero),
            "voice",
            "corr-1");

    [Fact]
    public async Task Granted_UpdatesSessionAndAuditsInSameTransaction()
    {
        _sessions.Setup(s => s.RecordConsentAsync(
                TenantId, SessionId, DomainConsentState.Granted,
                "us-ca-two-party:2026-01", "jp_us_ca", true,
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await BuildHandler().Handle(MakeCommand(), CancellationToken.None);

        Assert.Equal(SessionId, result.SessionId);
        Assert.True(result.RecordingAllowed);
        _audit.Verify(a => a.WriteAsync(
                It.Is<AuditRecord>(r =>
                    r.TenantId == TenantId
                    && r.Action == "consent.recorded"
                    && r.TargetId == SessionId
                    && r.ActorType == "customer"
                    && r.Decision == "allow"),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _uow.Verify(u => u.ExecuteInTransactionAsync(
            It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnknownSession_ThrowsNotFound_AndDoesNotAudit()
    {
        _sessions.Setup(s => s.RecordConsentAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DomainConsentState>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        await Assert.ThrowsAsync<ConversationSessionNotFoundException>(
            () => BuildHandler().Handle(MakeCommand(), CancellationToken.None));

        _audit.Verify(a => a.WriteAsync(It.IsAny<AuditRecord>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("declined", false, DomainConsentState.Declined)]
    [InlineData("not_required", true, DomainConsentState.NotRequired)]
    [InlineData("alternative_offered", false, DomainConsentState.AlternativeOffered)]
    public async Task OutcomeWireValues_MapToTheRatifiedPgEnum(
        string outcome, bool recordingAllowed, DomainConsentState expected)
    {
        _sessions.Setup(s => s.RecordConsentAsync(
                TenantId, SessionId, expected,
                It.IsAny<string>(), It.IsAny<string>(), recordingAllowed,
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await BuildHandler()
            .Handle(MakeCommand(outcome, recordingAllowed), CancellationToken.None);

        Assert.Equal(recordingAllowed, result.RecordingAllowed);
        _sessions.VerifyAll();
    }

    [Theory]
    [InlineData("granted", true, true)]
    [InlineData("declined", false, true)]
    [InlineData("declined", true, false)]        // recording cannot be allowed on decline
    [InlineData("alternative_offered", true, false)]
    [InlineData("maybe", false, false)]          // free-form outcome rejected (C9 family)
    [InlineData("", false, false)]
    public void Validator_EnforcesClosedOutcomeSetAndRecordingConsistency(
        string outcome, bool recordingAllowed, bool expectedValid)
    {
        var validator = new RecordConsentValidator();
        var result = validator.Validate(MakeCommand(outcome, recordingAllowed));
        Assert.Equal(expectedValid, result.IsValid);
    }

    [Fact]
    public void Validator_RequiresSessionTenantPolicyAndChannel()
    {
        var validator = new RecordConsentValidator();
        var bad = new RecordConsentCommand(
            "", "", "granted", "", "", true, default, "carrier-pigeon", null);
        var result = validator.Validate(bad);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RecordConsentCommand.TenantId));
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RecordConsentCommand.SessionId));
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RecordConsentCommand.ConsentPolicyVersion));
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RecordConsentCommand.JurisdictionPolicyId));
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(RecordConsentCommand.NoticeChannel));
    }
}
