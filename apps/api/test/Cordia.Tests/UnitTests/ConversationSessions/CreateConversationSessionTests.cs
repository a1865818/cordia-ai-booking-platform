using Cordia.Application.Handlers.ConversationSessions.CreateConversationSession;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.ConversationSessions;

/// <summary>
/// Unit tests for the spec-15 D12 single-writer session create
/// (<see cref="CreateConversationSessionCommandHandler"/>): tenant_id-leading identity, consent
/// fields null, StartedAt stamped from the clock, Version 0, and idempotency delegated to the repo.
/// </summary>
[Trait("Category", "Unit")]
public class CreateConversationSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IConversationSessionRepository> _sessions = new();

    private CreateConversationSessionCommandHandler BuildHandler()
        => new(_sessions.Object, new FixedClock(Now));

    private static CreateConversationSessionCommand MakeCommand()
        => new("tenant-abc", "sess_123", TenantChannel.Voice, "cep_tenant-abc", "corr_xyz");

    [Fact]
    public async Task Insert_StampsStartedAtVersionZero_ConsentNull_TenantIdLeading()
    {
        ConversationSession? inserted = null;
        _sessions.Setup(s => s.CreateAsync(It.IsAny<ConversationSession>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationSession, CancellationToken>((s, _) => inserted = s)
            .ReturnsAsync((ConversationSession s, CancellationToken _) => s);

        var dto = await BuildHandler().Handle(MakeCommand(), CancellationToken.None);

        Assert.NotNull(inserted);
        Assert.Equal("tenant-abc", inserted!.TenantId);
        Assert.Equal("sess_123", inserted.SessionId);
        Assert.Equal(TenantChannel.Voice, inserted.Channel);
        // channel_endpoint_id / correlation_id are persisted on the session row (spec-15 §5.4).
        Assert.Equal("cep_tenant-abc", inserted.ChannelEndpointId);
        Assert.Equal("corr_xyz", inserted.CorrelationId);
        Assert.Equal(Now, inserted.StartedAt);
        Assert.Equal(0, inserted.Version);
        // Consent fields are left null for the spec-14 consent gate to UPDATE later (D12).
        Assert.Null(inserted.ConsentState);
        Assert.Null(inserted.ConsentPolicyVersion);
        Assert.Null(inserted.JurisdictionPolicyId);
        Assert.Null(inserted.RecordingAllowed);
        Assert.Null(inserted.ConsentRecordedAt);
        Assert.Null(inserted.EndedAt);

        // The DTO echoes the identity + the (unpersisted) endpoint/correlation the command carried.
        Assert.Equal("tenant-abc", dto.TenantId);
        Assert.Equal("sess_123", dto.SessionId);
        Assert.Equal("cep_tenant-abc", dto.ChannelEndpointId);
        Assert.Equal("corr_xyz", dto.CorrelationId);
        Assert.Equal(Now, dto.StartedAt);
        Assert.Equal(0, dto.Version);
    }

    [Fact]
    public async Task Idempotent_ReturnsRepoPersistedRow_OnDuplicate()
    {
        // The repo swallows a unique-violation and returns the PRE-EXISTING row (earlier StartedAt).
        var existing = new ConversationSession
        {
            TenantId = "tenant-abc",
            SessionId = "sess_123",
            Channel = TenantChannel.Voice,
            StartedAt = Now.AddMinutes(-5),
            Version = 3,
        };
        _sessions.Setup(s => s.CreateAsync(It.IsAny<ConversationSession>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var dto = await BuildHandler().Handle(MakeCommand(), CancellationToken.None);

        // No throw — the handler surfaces the persisted row the repo returned.
        Assert.Equal(existing.StartedAt, dto.StartedAt);
        Assert.Equal(3, dto.Version);
        _sessions.Verify(s => s.CreateAsync(It.IsAny<ConversationSession>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
