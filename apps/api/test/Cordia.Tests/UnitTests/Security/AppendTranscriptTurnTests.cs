using System.Text;
using Cordia.Application.Common.Audit;
using Cordia.Application.Handlers.Security.AppendTranscriptTurn;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.Security;

/// <summary>
/// The spec-14 transcript producer (§5.1/§5.2): consent-gated fail-closed (absent/false/null
/// consent ⇒ NOTHING persisted), Inv-12 re-sanitization at the boundary, one
/// <c>transcript.recording_started</c> audit on the session's first persisted turn only, and
/// 404 for an unknown session.
/// </summary>
[Trait("Category", "Unit")]
public class AppendTranscriptTurnTests
{
    private const string Tenant = "t-1";
    private const string SessionId = "sess-1";

    private readonly Mock<IConversationSessionRepository> _sessions = new();
    private readonly Mock<IObjectStore> _store = new();
    private readonly Mock<IAuditWriter> _audit = new();

    private AppendTranscriptTurnCommandHandler Handler()
        => new(_sessions.Object, _store.Object, _audit.Object);

    private void SetupSession(bool? recordingAllowed)
        => _sessions.Setup(s => s.GetAsync(Tenant, SessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationSession
            {
                TenantId = Tenant,
                SessionId = SessionId,
                Channel = TenantChannel.Voice,
                RecordingAllowed = recordingAllowed,
                StartedAt = DateTimeOffset.UtcNow,
            });

    private static AppendTranscriptTurnCommand Command(int seq = 1, string text = "table for two")
        => new(Tenant, SessionId, seq, $"turn_{seq}", "customer", text);

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task ConsentNotGranted_SuppressesTheTurn_AndTouchesNothing(bool? recordingAllowed)
    {
        SetupSession(recordingAllowed);

        var result = await Handler().Handle(Command(), CancellationToken.None);

        Assert.False(result.Persisted);
        _store.VerifyNoOtherCalls();
        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnknownSession_Throws404()
    {
        _sessions.Setup(s => s.GetAsync(Tenant, SessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConversationSession?)null);

        await Assert.ThrowsAsync<ConversationSessionNotFoundException>(
            () => Handler().Handle(Command(), CancellationToken.None));
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FirstTurn_Persists_WritesRecordingStartedAudit_AndSanitizesText()
    {
        SetupSession(recordingAllowed: true);
        _store.Setup(s => s.ListIdsAsync(Tenant, "transcript", $"{SessionId}.", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        byte[]? stored = null;
        _store.Setup(s => s.PutAsync(Tenant, "transcript", $"{SessionId}.000001",
                It.IsAny<ReadOnlyMemory<byte>>(), null, It.IsAny<CancellationToken>()))
            .Callback<string, string, string, ReadOnlyMemory<byte>, TimeSpan?, CancellationToken>(
                (_, _, _, content, _, _) => stored = content.ToArray())
            .Returns(Task.CompletedTask);

        // Inv-12: a PAN that slipped past the agent-side redaction must be masked at rest.
        var result = await Handler().Handle(
            Command(seq: 1, text: "card 4111111111111111 please"), CancellationToken.None);

        Assert.True(result.Persisted);
        Assert.NotNull(stored);
        var json = Encoding.UTF8.GetString(stored);
        Assert.DoesNotContain("4111111111111111", json);
        Assert.Contains("[REDACTED]", json);
        Assert.Contains("turn_1", json);

        _audit.Verify(a => a.WriteAsync(
                It.Is<AuditRecord>(r =>
                    r.Action == "transcript.recording_started"
                    && r.TargetId == SessionId
                    && r.Decision == "allow"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SubsequentTurn_Persists_WithoutAnotherAudit()
    {
        SetupSession(recordingAllowed: true);
        _store.Setup(s => s.ListIdsAsync(Tenant, "transcript", $"{SessionId}.", It.IsAny<CancellationToken>()))
            .ReturnsAsync([$"{SessionId}.000001"]);
        _store.Setup(s => s.PutAsync(Tenant, "transcript", $"{SessionId}.000002",
                It.IsAny<ReadOnlyMemory<byte>>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await Handler().Handle(Command(seq: 2), CancellationToken.None);

        Assert.True(result.Persisted);
        _audit.VerifyNoOtherCalls();
    }
}
