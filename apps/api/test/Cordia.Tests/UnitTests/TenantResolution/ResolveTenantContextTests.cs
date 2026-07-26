using System.Text.Json;
using Cordia.Application.Common.Audit;
using Cordia.Application.Handlers.ConversationSessions.CreateConversationSession;
using Cordia.Application.Handlers.TenantResolution.ResolveTenantContext;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Observability;
using Cordia.Domain.TenantResolution;
using Cordia.Infrastructure.Services;
using MediatR;
using Microsoft.Extensions.Options;
using Moq;

namespace Cordia.Tests.UnitTests.TenantResolution;

/// <summary>
/// Unit tests for the spec-15 channel-gateway core (<see cref="ResolveTenantContextCommandHandler"/>):
/// exactly-one resolution, fail-closed on zero/many, correlation echo/mint, sign+session issuance,
/// and an audit row on EVERY outcome.
/// </summary>
[Trait("Category", "Unit")]
public class ResolveTenantContextTests
{
    private const int TtlSeconds = 300;
    private static readonly DateTimeOffset Now = new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<ITenantChannelEndpointRepository> _endpoints = new();
    private readonly Mock<ITenantContextSigner> _signer = new();
    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<IAuditWriter> _audit = new();
    private readonly Mock<ICorrelationContext> _correlation = new();
    private readonly List<AuditRecord> _audits = [];

    public ResolveTenantContextTests()
    {
        _signer.Setup(s => s.Sign(It.IsAny<TenantContextClaims>())).Returns("header.payload.signature");
        _correlation.Setup(c => c.Current).Returns(AmbientCorrelationContext.Empty);
        _mediator.Setup(m => m.Send(It.IsAny<CreateConversationSessionCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IRequest<ConversationSessionDto> req, CancellationToken _) =>
            {
                var c = (CreateConversationSessionCommand)req;
                return new ConversationSessionDto(
                    c.TenantId, c.SessionId, c.Channel, c.ChannelEndpointId, c.CorrelationId, Now, 0);
            });
        _audit.Setup(a => a.WriteAsync(It.IsAny<AuditRecord>(), It.IsAny<CancellationToken>()))
            .Returns((AuditRecord r, CancellationToken _) => { _audits.Add(r); return Task.CompletedTask; });
    }

    private ResolveTenantContextCommandHandler BuildHandler()
        => new(
            _endpoints.Object,
            _signer.Object,
            _mediator.Object,
            _audit.Object,
            _correlation.Object,
            Options.Create(new TenantContextOptions { TtlSeconds = TtlSeconds }),
            new FixedClock(Now));

    private static TenantChannelEndpoint Endpoint(
        string tenantId, TenantChannel channel, SignalType signalType, string signalValue) => new()
        {
            TenantId = tenantId,
            ChannelEndpointId = $"cep_{tenantId}",
            Channel = channel,
            SignalType = signalType,
            SignalValue = signalValue,
            ConfigurationVersion = "cfg-v7",
            PolicyBundleId = "bundle-42",
            Status = EndpointStatus.Active,
        };

    private void SetupResolve(TenantChannel channel, SignalType signalType, string signalValue,
        params TenantChannelEndpoint[] rows)
        => _endpoints.Setup(r => r.ResolveAllActiveAsync(channel, signalType, signalValue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(rows);

    private static string ExtraString(AuditRecord r, string key)
        => JsonSerializer.SerializeToNode(r.Extra)!.AsObject()[key]!.GetValue<string>();

    [Theory]
    [InlineData(SignalType.PhoneNumber, TenantChannel.Voice, "+15550001111", "tenant-voice")]
    [InlineData(SignalType.MessagingRecipient, TenantChannel.Messaging, "page-99", "tenant-msg")]
    [InlineData(SignalType.WidgetKey, TenantChannel.Web, "widget-abc", "tenant-web")]
    [InlineData(SignalType.EmailRecipient, TenantChannel.Email, "hi@biz.example", "tenant-email")]
    [InlineData(SignalType.WebOrigin, TenantChannel.Web, "https://biz.example", "tenant-origin")]
    [InlineData(SignalType.DirectorySlug, TenantChannel.Voice, "acme-diner", "tenant-dir")]
    public async Task EachSignalType_ResolvesTheCorrectSingleTenant(
        SignalType signalType, TenantChannel channel, string signalValue, string tenantId)
    {
        var endpoint = Endpoint(tenantId, channel, signalType, signalValue);
        SetupResolve(channel, signalType, signalValue, endpoint);

        var result = await BuildHandler().Handle(
            new ResolveTenantContextCommand(channel, signalType, signalValue, InboundCorrelationId: null),
            CancellationToken.None);

        Assert.Equal(tenantId, result.Claims.TenantId);
        Assert.Equal(endpoint.ChannelEndpointId, result.Claims.ChannelEndpointId);
        Assert.Equal(channel, result.Claims.Channel);
        Assert.Equal("cfg-v7", result.Claims.ConfigurationVersion);
        Assert.Equal("header.payload.signature", result.Jws);
        Assert.StartsWith("sess_", result.Claims.SessionId);
        Assert.Equal(Now, result.Claims.IssuedAt);
        Assert.Equal(Now.AddSeconds(TtlSeconds), result.Claims.ExpiresAt);

        // A resolved session row is created for the resolved tenant (D12 single writer).
        _mediator.Verify(m => m.Send(
            It.Is<CreateConversationSessionCommand>(c =>
                c.TenantId == tenantId
                && c.Channel == channel
                && c.ChannelEndpointId == endpoint.ChannelEndpointId
                && c.SessionId == result.Claims.SessionId
                && c.CorrelationId == result.Claims.CorrelationId),
            It.IsAny<CancellationToken>()), Times.Once);

        // Exactly one audit row, outcome=resolved, allow, carrying the resolved tenant + endpoint.
        var record = Assert.Single(_audits);
        Assert.Equal("tenant.resolution", record.Action);
        Assert.Equal("allow", record.Decision);
        Assert.Equal(tenantId, record.TenantId);
        Assert.Equal(endpoint.ChannelEndpointId, record.TargetId);
        Assert.Equal("resolved", ExtraString(record, "outcome"));
        Assert.Equal(tenantId, ExtraString(record, "tenant_id"));
        Assert.Equal(endpoint.ChannelEndpointId, ExtraString(record, "channel_endpoint_id"));
    }

    [Fact]
    public async Task AuditMetadata_SerializesSignalTypeToWireForm()
    {
        var endpoint = Endpoint("tenant-voice", TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111");
        SetupResolve(TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111", endpoint);

        await BuildHandler().Handle(
            new ResolveTenantContextCommand(TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111", null),
            CancellationToken.None);

        var record = Assert.Single(_audits);
        Assert.Equal("phone_number", ExtraString(record, "signal_type"));
        Assert.Equal("voice", ExtraString(record, "channel"));
    }

    [Fact]
    public async Task PinsPolicyIds_FromPolicyBundle_IntoBothClaimLists()
    {
        var endpoint = Endpoint("tenant-voice", TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111");
        SetupResolve(TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111", endpoint);

        var result = await BuildHandler().Handle(
            new ResolveTenantContextCommand(TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111", null),
            CancellationToken.None);

        Assert.Equal(["bundle-42"], result.Claims.AllowedAgentPolicyIds);
        Assert.Equal(["bundle-42"], result.Claims.AllowedToolPolicyIds);
    }

    [Fact]
    public async Task ZeroMatches_FailsClosed_ThrowsTenantNotResolved_AuditsOutcome_NoContext()
    {
        SetupResolve(TenantChannel.Voice, SignalType.PhoneNumber, "+15550000000"); // no rows

        await Assert.ThrowsAsync<TenantNotResolvedException>(() => BuildHandler().Handle(
            new ResolveTenantContextCommand(TenantChannel.Voice, SignalType.PhoneNumber, "+15550000000", null),
            CancellationToken.None));

        _signer.Verify(s => s.Sign(It.IsAny<TenantContextClaims>()), Times.Never);
        _mediator.Verify(m => m.Send(It.IsAny<CreateConversationSessionCommand>(), It.IsAny<CancellationToken>()), Times.Never);

        var record = Assert.Single(_audits);
        Assert.Equal("deny", record.Decision);
        Assert.Equal(string.Empty, record.TenantId);
        Assert.Equal("tenant_not_resolved", ExtraString(record, "outcome"));
    }

    [Fact]
    public async Task ManyMatches_FailsClosed_ThrowsAmbiguous_AuditsOutcome_NoContext()
    {
        var a = Endpoint("tenant-a", TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111");
        var b = Endpoint("tenant-b", TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111");
        SetupResolve(TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111", a, b);

        await Assert.ThrowsAsync<AmbiguousEndpointException>(() => BuildHandler().Handle(
            new ResolveTenantContextCommand(TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111", null),
            CancellationToken.None));

        _signer.Verify(s => s.Sign(It.IsAny<TenantContextClaims>()), Times.Never);
        _mediator.Verify(m => m.Send(It.IsAny<CreateConversationSessionCommand>(), It.IsAny<CancellationToken>()), Times.Never);

        var record = Assert.Single(_audits);
        Assert.Equal("deny", record.Decision);
        Assert.Equal(string.Empty, record.TenantId);
        Assert.Equal("ambiguous_endpoint", ExtraString(record, "outcome"));
    }

    [Fact]
    public async Task ValidInboundCorrelation_IsEchoed_EverywhereOnce()
    {
        const string inbound = "corr_inbound_123";
        var endpoint = Endpoint("tenant-voice", TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111");
        SetupResolve(TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111", endpoint);

        var result = await BuildHandler().Handle(
            new ResolveTenantContextCommand(TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111", inbound),
            CancellationToken.None);

        Assert.Equal(inbound, result.Claims.CorrelationId);
        _mediator.Verify(m => m.Send(
            It.Is<CreateConversationSessionCommand>(c => c.CorrelationId == inbound), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal(inbound, Assert.Single(_audits).CorrelationId);
        // The inbound id is echoed verbatim — never re-minted into a corr_ id.
        _correlation.Verify(c => c.Current, Times.Never);
    }

    [Fact]
    public async Task MalformedInboundCorrelation_IsRejected_AmbientUsed()
    {
        var endpoint = Endpoint("tenant-voice", TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111");
        SetupResolve(TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111", endpoint);
        _correlation.Setup(c => c.Current).Returns(
            new AmbientCorrelationContext(TenantId: string.Empty, CorrelationId: "corr_ambient_9"));

        var result = await BuildHandler().Handle(
            new ResolveTenantContextCommand(TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111",
                InboundCorrelationId: "has spaces / and slashes"),
            CancellationToken.None);

        Assert.Equal("corr_ambient_9", result.Claims.CorrelationId);
    }

    [Fact]
    public async Task AbsentCorrelation_MintedOnce_NeverReminted()
    {
        var endpoint = Endpoint("tenant-voice", TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111");
        SetupResolve(TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111", endpoint);
        // No inbound id and an empty ambient context ⇒ mint exactly one.

        var result = await BuildHandler().Handle(
            new ResolveTenantContextCommand(TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111", null),
            CancellationToken.None);

        var minted = result.Claims.CorrelationId;
        Assert.StartsWith("corr_", minted);

        // The one minted id threads the claims, the session command, and the audit row — one mint.
        _mediator.Verify(m => m.Send(
            It.Is<CreateConversationSessionCommand>(c => c.CorrelationId == minted), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal(minted, Assert.Single(_audits).CorrelationId);
    }

    [Fact]
    public async Task SigningFailure_FailsClosed_Audited_NoSessionCreated()
    {
        var endpoint = Endpoint("tenant-voice", TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111");
        SetupResolve(TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111", endpoint);
        _signer.Setup(s => s.Sign(It.IsAny<TenantContextClaims>())).Throws(new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => BuildHandler().Handle(
            new ResolveTenantContextCommand(TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111", null),
            CancellationToken.None));

        // Fail closed: no session row, and the failure is audited under the resolved tenant.
        _mediator.Verify(m => m.Send(It.IsAny<CreateConversationSessionCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        var record = Assert.Single(_audits);
        Assert.Equal("deny", record.Decision);
        Assert.Equal("tenant-voice", record.TenantId);
        Assert.Equal("signing_failed", ExtraString(record, "outcome"));
    }

    [Fact]
    public async Task SessionInsertFailure_FailsClosed_Audited_ContextNotReturned()
    {
        var endpoint = Endpoint("tenant-voice", TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111");
        SetupResolve(TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111", endpoint);
        _mediator.Setup(m => m.Send(It.IsAny<CreateConversationSessionCommand>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => BuildHandler().Handle(
            new ResolveTenantContextCommand(TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111", null),
            CancellationToken.None));

        // The context was minted (signed) but never returned — fail closed, audited.
        _signer.Verify(s => s.Sign(It.IsAny<TenantContextClaims>()), Times.Once);
        var record = Assert.Single(_audits);
        Assert.Equal("deny", record.Decision);
        Assert.Equal("tenant-voice", record.TenantId);
        Assert.Equal("session_insert_failed", ExtraString(record, "outcome"));
    }

    [Fact]
    public async Task AuditOccurredAt_SharesOneTimestamp_WithSignedClaimsIssuedAt()
    {
        // A clock that advances on every read: if AuditAsync re-read the clock (instead of reusing the
        // `now` captured once for the signed claims), audit.OccurredAt would drift PAST claims.issued_at.
        // Asserting equality pins the invariant that the signed context and its audit row share one clock.
        var clock = new AdvancingClock(Now, TimeSpan.FromSeconds(1));
        var endpoint = Endpoint("tenant-voice", TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111");
        SetupResolve(TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111", endpoint);

        var handler = new ResolveTenantContextCommandHandler(
            _endpoints.Object, _signer.Object, _mediator.Object, _audit.Object, _correlation.Object,
            Options.Create(new TenantContextOptions { TtlSeconds = TtlSeconds }), clock);

        var result = await handler.Handle(
            new ResolveTenantContextCommand(TenantChannel.Voice, SignalType.PhoneNumber, "+15550001111", null),
            CancellationToken.None);

        var record = Assert.Single(_audits);
        Assert.Equal(result.Claims.IssuedAt, record.OccurredAt!.Value);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // Advances by <paramref name="step"/> on every read (read 0 ⇒ start, read 1 ⇒ start+step, …) so a
    // second clock read produces a strictly later time — exposing any drift between the two.
    private sealed class AdvancingClock(DateTimeOffset start, TimeSpan step) : TimeProvider
    {
        private int _reads;

        public override DateTimeOffset GetUtcNow() => start + step * _reads++;
    }
}
