using System.Text.Json;
using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.Security.GetTranscript;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.Security;

/// <summary>
/// The §3.1 spine on the transcript reader: no-credentials and role-mismatch deny (audited,
/// 403, Inv-11); grant path delegates to spec-08 <c>RequireGrantAsync</c> with the session's
/// OWN customer ref; consent absent/declined after valid authorization → deny audit +
/// <c>ConsentRequiredException</c> (409 — the first thrower); happy path assembles turns in
/// seq order and writes the allow audit with data_classes [pii, transcript].
/// </summary>
[Trait("Category", "Unit")]
public class GetTranscriptTests
{
    private const string Tenant = "t-1";
    private const string SessionId = "sess-1";
    private const string Customer = "cust-9";

    private readonly Mock<IConversationSessionRepository> _sessions = new();
    private readonly Mock<IGrantAuthorizationService> _grants = new();
    private readonly Mock<IObjectStore> _store = new();
    private readonly Mock<IAuditWriter> _audit = new();

    private GetTranscriptQueryHandler Handler()
        => new(_sessions.Object, _grants.Object, _store.Object, _audit.Object);

    private void SetupSession(bool? recordingAllowed, string? customerRef = Customer)
        => _sessions.Setup(s => s.GetAsync(Tenant, SessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationSession
            {
                TenantId = Tenant,
                SessionId = SessionId,
                Channel = TenantChannel.Voice,
                RecordingAllowed = recordingAllowed,
                CustomerRef = customerRef,
                StartedAt = DateTimeOffset.UtcNow,
            });

    private void SetupTurns(params (int Seq, string Text)[] turns)
    {
        _store.Setup(s => s.ListIdsAsync(Tenant, "transcript", $"{SessionId}.", It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. turns.Select(t => $"{SessionId}.{t.Seq:D6}")]);
        foreach (var (seq, text) in turns)
        {
            _store.Setup(s => s.GetAsync(Tenant, "transcript", $"{SessionId}.{seq:D6}", It.IsAny<CancellationToken>()))
                .ReturnsAsync(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    turn_id = $"turn_{seq}",
                    speaker = "customer",
                    text,
                    ts = DateTimeOffset.UtcNow,
                }));
        }
    }

    private static GetTranscriptQuery StaffQuery(string role = StaffRole.Operator)
        => new(Tenant, SessionId, GrantId: null, ActorId: "ops.jane", ActorRole: role, ReasonCode: "dispute_review");

    [Fact]
    public async Task NoCredentials_DeniesWithAuditAnd403_NeverTouchesTheStore()
    {
        SetupSession(recordingAllowed: true);

        // Inv-11: the session/customer identity alone never authorizes the read.
        await Assert.ThrowsAsync<AuthorizationFailedException>(() => Handler().Handle(
            new GetTranscriptQuery(Tenant, SessionId, null, null, null, null), CancellationToken.None));

        _audit.Verify(a => a.WriteAsync(
            It.Is<AuditRecord>(r => r.Decision == "deny" && r.Action == "read_sensitive_transcript"),
            It.IsAny<CancellationToken>()), Times.Once);
        _store.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("")]
    public async Task NonStaffRole_DeniesWithAuditAnd403(string role)
    {
        SetupSession(recordingAllowed: true);

        await Assert.ThrowsAsync<AuthorizationFailedException>(
            () => Handler().Handle(StaffQuery(role), CancellationToken.None));

        _audit.Verify(a => a.WriteAsync(
            It.Is<AuditRecord>(r => r.Decision == "deny"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConsentAbsent_AfterValidRole_Throws409_AndAuditsTheDenial()
    {
        SetupSession(recordingAllowed: null);

        // The first ConsentRequiredException call site (spec 14 §5.2 → 409).
        var ex = await Assert.ThrowsAsync<ConsentRequiredException>(
            () => Handler().Handle(StaffQuery(), CancellationToken.None));

        Assert.Equal("transcript", ex.RequiredScope);
        _audit.Verify(a => a.WriteAsync(
            It.Is<AuditRecord>(r => r.Decision == "deny"),
            It.IsAny<CancellationToken>()), Times.Once);
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StaffRole_ConsentGranted_AssemblesTurnsInOrder_AndWritesAllowAudit()
    {
        SetupSession(recordingAllowed: true);
        SetupTurns((2, "seven pm works"), (1, "table for two"));

        var view = await Handler().Handle(StaffQuery(), CancellationToken.None);

        Assert.Equal([1, 2], view.Turns.Select(t => t.Seq));
        Assert.Equal("table for two", view.Turns[0].Text);
        _audit.Verify(a => a.WriteAsync(
            It.Is<AuditRecord>(r =>
                r.Decision == "allow"
                && r.Action == "read_sensitive_transcript"
                && r.ActorType == StaffRole.Operator
                && r.DataClasses.Contains("pii")
                && r.DataClasses.Contains("transcript")
                && r.ReasonCode == "dispute_review"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OwnerRole_AlsoReads_Gd1Matrix()
    {
        SetupSession(recordingAllowed: true);
        SetupTurns((1, "hello"));

        var view = await Handler().Handle(StaffQuery(StaffRole.Owner), CancellationToken.None);

        Assert.Single(view.Turns);
    }

    [Fact]
    public async Task GrantPath_RequiresTheSessionsOwnCustomer_AndPropagates403FromSpec08()
    {
        SetupSession(recordingAllowed: true);
        _grants.Setup(g => g.RequireGrantAsync(
                Tenant, "grant-1", Customer, VerificationActionEnum.ReadSensitive, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VerificationRequiredException(Tenant, Customer, VerificationActionEnum.ReadSensitive, null));

        // The spec-08 service already deny-audited; the 403 propagates untouched.
        await Assert.ThrowsAsync<VerificationRequiredException>(() => Handler().Handle(
            new GetTranscriptQuery(Tenant, SessionId, "grant-1", null, null, null), CancellationToken.None));

        _grants.Verify(g => g.RequireGrantAsync(
            Tenant, "grant-1", Customer, VerificationActionEnum.ReadSensitive, null, It.IsAny<CancellationToken>()), Times.Once);
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GrantPath_Valid_Reads_AndAuditsAsCustomer()
    {
        SetupSession(recordingAllowed: true);
        SetupTurns((1, "hello"));
        _grants.Setup(g => g.RequireGrantAsync(
                Tenant, "grant-1", Customer, VerificationActionEnum.ReadSensitive, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VerificationGrant { TenantId = Tenant, GrantId = "grant-1", CustomerRef = Customer });

        var view = await Handler().Handle(
            new GetTranscriptQuery(Tenant, SessionId, "grant-1", null, null, null), CancellationToken.None);

        Assert.Single(view.Turns);
        _audit.Verify(a => a.WriteAsync(
            It.Is<AuditRecord>(r =>
                r.Decision == "allow" && r.ActorType == "customer" && r.ActorId == Customer),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnknownSession_Throws404()
    {
        _sessions.Setup(s => s.GetAsync(Tenant, SessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConversationSession?)null);

        await Assert.ThrowsAsync<ConversationSessionNotFoundException>(
            () => Handler().Handle(StaffQuery(), CancellationToken.None));
    }
}
