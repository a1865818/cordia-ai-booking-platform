#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken

using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.TenantResolution;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cordia.Tests.IntegrationTests.TenantResolution;

/// <summary>
/// Spec-15 Phase 4 channel-gateway ingress integration tests (Alba + Testcontainers/local Postgres,
/// Category=Integration). Full end-to-end path: POST a channel webhook → resolve the tenant from the
/// transport-attested signal → mint + sign a <c>TenantContext</c> JWS → create exactly one
/// <c>conversation_sessions</c> row → append the <c>tenant.resolution</c> audit row; or fail closed.
///
/// Covers: full telephony + messaging ingress; zero-match 422 fail-closed with audit + no session;
/// one correlation_id threaded onto both the session and the audit row (minted + inbound-echoed);
/// Inv-13 ordering (a tenant-scoped endpoint fails closed at TenantGuard without a verified context,
/// and resolves WITH one); a bad signed context ⇒ 401; and the reviewer-mandated non-unique
/// session-insert fail-closed path (session_insert_failed audited, no session row).
///
/// The ambiguous-endpoint 422 path is deliberately NOT provoked end-to-end here — see
/// <see cref="AmbiguousEndpoint_IsStructurallyPreventedBy_PartialUniqueIndex"/> for why (it is
/// unreachable while <c>uq_channel_signal_active</c> exists; the handler branch + its
/// <c>ambiguous_endpoint</c> audit are covered by the Phase 3 unit tests, which mock the repository
/// to return two rows).
/// </summary>
[Trait("Category", "Integration")]
[Collection("TenantResolutionApi")]
public class TenantResolutionIngressTests(TenantResolutionApiFixture fixture)
{
    private const string TelephonyUrl = "/api/channels/telephony";
    private const string MessagingUrl = "/api/channels/messaging";
    private const string ResolutionEventType = "tenant.resolution";

    // ── Seeding ───────────────────────────────────────────────────────────────────────────────────

    private async Task SeedEndpointAsync(
        string tenantId,
        string endpointId,
        TenantChannel channel,
        SignalType signalType,
        string signalValue,
        EndpointStatus status = EndpointStatus.Active,
        string configurationVersion = "cfg-v1",
        string policyBundleId = "policy-001")
    {
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.TenantChannelEndpoints.Add(new TenantChannelEndpoint
        {
            TenantId = tenantId,
            ChannelEndpointId = endpointId,
            Channel = channel,
            SignalType = signalType,
            SignalValue = signalValue,
            ConfigurationVersion = configurationVersion,
            PolicyBundleId = policyBundleId,
            Status = status,
            Version = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private static string TelephonyBody(string? to, string? from = "+15550009999", string? callerName = "SPOOFED NAME")
        => JsonSerializer.Serialize(new { to, from, caller_name = callerName });

    private static string MessagingBody(string? recipientId, string? senderId = "sender-xyz", string? senderName = "SPOOFED NAME")
        => JsonSerializer.Serialize(new { recipient_id = recipientId, sender_id = senderId, sender_name = senderName });

    // ── Full telephony ingress ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Telephony_FullIngress_Returns200_JwsVerifies_CreatesExactlyOneSession_WithAuditAndCorrelation()
    {
        var tenantId = $"tr-tel-{Guid.NewGuid():N}";
        var endpointId = $"ep-{Guid.NewGuid():N}";
        var did = $"+1555{Guid.NewGuid():N}"[..14];

        await SeedEndpointAsync(tenantId, endpointId, TenantChannel.Voice, SignalType.PhoneNumber, did);

        var result = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(TelephonyBody(did)).ToUrl(TelephonyUrl);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        // Parse the response: JWS + decoded claims.
        var (jws, claims) = ReadContext(await result.ReadAsTextAsync());

        Assert.False(string.IsNullOrEmpty(jws));
        Assert.Equal(tenantId, claims.TenantId);
        Assert.Equal(endpointId, claims.ChannelEndpointId);
        Assert.Equal("voice", claims.Channel);
        Assert.False(string.IsNullOrEmpty(claims.SessionId));
        Assert.False(string.IsNullOrEmpty(claims.CorrelationId));

        // The returned JWS must verify via the SAME signer the gateway used, and carry the same tenant.
        var signer = fixture.Host.Services.GetRequiredService<ITenantContextSigner>();
        var verified = signer.Verify(jws);
        Assert.Equal(tenantId, verified.TenantId);
        Assert.Equal(claims.SessionId, verified.SessionId);
        Assert.Equal(claims.CorrelationId, verified.CorrelationId);
        Assert.Equal(TenantChannel.Voice, verified.Channel);

        // Exactly ONE conversation_sessions row, tenant_id leading the PK, consent fields null,
        // correlation_id populated.
        await using var ctx = fixture.CreateContext(tenantId);
        var sessions = await ctx.ConversationSessions.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .ToListAsync();

        var session = Assert.Single(sessions);
        Assert.Equal(tenantId, session.TenantId);           // tenant_id leads the (tenant_id, session_id) PK
        Assert.Equal(claims.SessionId, session.SessionId);
        Assert.Equal(TenantChannel.Voice, session.Channel);
        Assert.Equal(endpointId, session.ChannelEndpointId);
        Assert.Equal(claims.CorrelationId, session.CorrelationId);
        // Consent fields left null for the spec-14 consent gate (D12).
        Assert.Null(session.ConsentState);
        Assert.Null(session.ConsentPolicyVersion);
        Assert.Null(session.RecordingAllowed);
        Assert.Null(session.ConsentRecordedAt);
        Assert.Null(session.JurisdictionPolicyId);

        // Exactly ONE tenant.resolution audit row for this correlation, outcome=resolved, same tenant.
        var audit = await SingleAuditByCorrelationAsync(ctx, claims.CorrelationId);
        AssertAuditOutcome(audit, expectedOutcome: "resolved", expectedTenantId: tenantId);

        // ONE correlation_id on BOTH the session row and the audit row.
        Assert.Equal(session.CorrelationId, audit.CorrelationId);
        Assert.Equal(claims.CorrelationId, audit.CorrelationId);
    }

    // ── Full messaging ingress ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Messaging_FullIngress_Returns200_JwsVerifies_CreatesExactlyOneSession_WithAuditAndCorrelation()
    {
        var tenantId = $"tr-msg-{Guid.NewGuid():N}";
        var endpointId = $"ep-{Guid.NewGuid():N}";
        var recipient = $"page-{Guid.NewGuid():N}";

        await SeedEndpointAsync(tenantId, endpointId, TenantChannel.Messaging, SignalType.MessagingRecipient, recipient);

        var result = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(MessagingBody(recipient)).ToUrl(MessagingUrl);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var (jws, claims) = ReadContext(await result.ReadAsTextAsync());

        Assert.Equal(tenantId, claims.TenantId);
        Assert.Equal(endpointId, claims.ChannelEndpointId);
        Assert.Equal("messaging", claims.Channel);

        var signer = fixture.Host.Services.GetRequiredService<ITenantContextSigner>();
        var verified = signer.Verify(jws);
        Assert.Equal(tenantId, verified.TenantId);
        Assert.Equal(TenantChannel.Messaging, verified.Channel);

        await using var ctx = fixture.CreateContext(tenantId);
        var sessions = await ctx.ConversationSessions.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .ToListAsync();

        var session = Assert.Single(sessions);
        Assert.Equal(claims.SessionId, session.SessionId);
        Assert.Equal(TenantChannel.Messaging, session.Channel);
        Assert.Equal(endpointId, session.ChannelEndpointId);
        Assert.Null(session.ConsentState);
        Assert.Null(session.RecordingAllowed);

        var audit = await SingleAuditByCorrelationAsync(ctx, claims.CorrelationId);
        AssertAuditOutcome(audit, expectedOutcome: "resolved", expectedTenantId: tenantId);
        Assert.Equal(session.CorrelationId, audit.CorrelationId);
    }

    // ── Zero-match fail-closed (tenant_not_resolved) → 422, no session, audited ─────────────────────

    [Fact]
    public async Task Telephony_NoMatchingEndpoint_Returns422_NoSession_AuditsTenantNotResolved()
    {
        var did = $"+1555{Guid.NewGuid():N}"[..14]; // never seeded
        var correlationId = $"corr_{Guid.NewGuid():N}";

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(TelephonyBody(did)).ToUrl(TelephonyUrl);
            // Supply our own correlation id so we can find the fail-closed audit row deterministically.
            x.WithRequestHeader("X-Correlation-Id", correlationId);
            x.StatusCodeShouldBe(HttpStatusCode.UnprocessableEntity);
        });

        await using var ctx = fixture.CreateContext(string.Empty); // failure audit rows carry tenant_id=""

        // No session row was created for this resolution.
        var anySession = await ctx.ConversationSessions.AsNoTracking()
            .IgnoreQueryFilters()
            .AnyAsync(s => s.CorrelationId == correlationId);
        Assert.False(anySession);

        // Exactly one tenant.resolution audit row, outcome=tenant_not_resolved, empty tenant (no fallback).
        var audit = await SingleAuditByCorrelationAsync(ctx, correlationId, ignoreQueryFilters: true);
        AssertAuditOutcome(audit, expectedOutcome: "tenant_not_resolved", expectedTenantId: string.Empty);
    }

    [Fact]
    public async Task Messaging_NoMatchingEndpoint_Returns422_NoSession_AuditsTenantNotResolved()
    {
        var recipient = $"page-{Guid.NewGuid():N}"; // never seeded
        var correlationId = $"corr_{Guid.NewGuid():N}";

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(MessagingBody(recipient)).ToUrl(MessagingUrl);
            x.WithRequestHeader("X-Correlation-Id", correlationId);
            x.StatusCodeShouldBe(HttpStatusCode.UnprocessableEntity);
        });

        await using var ctx = fixture.CreateContext(string.Empty);

        var anySession = await ctx.ConversationSessions.AsNoTracking()
            .IgnoreQueryFilters()
            .AnyAsync(s => s.CorrelationId == correlationId);
        Assert.False(anySession);

        var audit = await SingleAuditByCorrelationAsync(ctx, correlationId, ignoreQueryFilters: true);
        AssertAuditOutcome(audit, expectedOutcome: "tenant_not_resolved", expectedTenantId: string.Empty);
    }

    // ── Inbound provider correlation is echoed onto session + audit ─────────────────────────────────

    [Fact]
    public async Task Telephony_InboundProviderCorrelation_IsEchoedOntoSessionAndAudit()
    {
        var tenantId = $"tr-corr-{Guid.NewGuid():N}";
        var endpointId = $"ep-{Guid.NewGuid():N}";
        var did = $"+1555{Guid.NewGuid():N}"[..14];
        var providerCorr = $"pcall{Guid.NewGuid():N}"; // well-formed: ^[A-Za-z0-9_-]{1,64}$

        await SeedEndpointAsync(tenantId, endpointId, TenantChannel.Voice, SignalType.PhoneNumber, did);

        var result = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(TelephonyBody(did)).ToUrl(TelephonyUrl);
            x.WithRequestHeader("X-Provider-Call-Id", providerCorr);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var (_, claims) = ReadContext(await result.ReadAsTextAsync());

        // The provider id is the resolution correlation id (echoed, not re-minted).
        Assert.Equal(providerCorr, claims.CorrelationId);

        await using var ctx = fixture.CreateContext(tenantId);
        var session = await ctx.ConversationSessions.AsNoTracking()
            .SingleAsync(s => s.TenantId == tenantId && s.SessionId == claims.SessionId);
        Assert.Equal(providerCorr, session.CorrelationId);

        var audit = await SingleAuditByCorrelationAsync(ctx, providerCorr);
        AssertAuditOutcome(audit, expectedOutcome: "resolved", expectedTenantId: tenantId);
        Assert.Equal(providerCorr, audit.CorrelationId);
        // ONE correlation id on both the session and the audit row.
        Assert.Equal(session.CorrelationId, audit.CorrelationId);
    }

    // ── Inv-13 ordering: a tenant-scoped endpoint fails closed WITHOUT a verified context ───────────

    [Fact]
    public async Task DownstreamTenantScopedEndpoint_WithoutVerifiedContext_FailsClosedAtTenantGuard()
    {
        // No X-Tenant-Id and no X-Tenant-Context ⇒ the request reaches a tenant-scoped endpoint with no
        // resolved tenant ⇒ TenantGuard.RequireTenant throws TenantScopeMissingException ⇒ 400. Proves
        // the fail-closed guard runs BEFORE any tenant-scoped data access (Inv-13 / Inv-11).
        var sessionId = $"sess_{Guid.NewGuid():N}";
        var body = JsonSerializer.Serialize(new { seq = 1, turn_id = "t1", speaker = "agent", text = "hello" });

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(body).ToUrl($"/api/conversation-sessions/{sessionId}/transcript-turns");
            // Intentionally NO X-Tenant-Id and NO X-Tenant-Context.
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [Fact]
    public async Task DownstreamTenantScopedEndpoint_WithVerifiedContext_ResolvesTenant()
    {
        // First mint a real signed context + session via a telephony resolution.
        var tenantId = $"tr-inv13-{Guid.NewGuid():N}";
        var endpointId = $"ep-{Guid.NewGuid():N}";
        var did = $"+1555{Guid.NewGuid():N}"[..14];

        await SeedEndpointAsync(tenantId, endpointId, TenantChannel.Voice, SignalType.PhoneNumber, did);

        var resolve = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(TelephonyBody(did)).ToUrl(TelephonyUrl);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        var (jws, claims) = ReadContext(await resolve.ReadAsTextAsync());

        // Now present the verified context to a tenant-scoped endpoint WITHOUT any X-Tenant-Id shim.
        // The verification middleware must set the tenant so the guard passes and the handler runs.
        var body = JsonSerializer.Serialize(new { seq = 1, turn_id = "t1", speaker = "agent", text = "hello" });
        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(body).ToUrl($"/api/conversation-sessions/{claims.SessionId}/transcript-turns");
            x.WithRequestHeader("X-Tenant-Context", jws);
            // No X-Tenant-Id — the tenant must come from the verified context alone.
            // Consent not recorded ⇒ the turn is suppressed (persisted=false) but the request is
            // authorized and returns 200 — i.e. NOT the 400 the guard would produce without a tenant.
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
    }

    [Fact]
    public async Task VerificationMiddleware_InvalidSignedContext_Returns401_BeforeTheController()
    {
        // A malformed / unverifiable X-Tenant-Context must be rejected by the middleware with 401 —
        // never reaching the controller (which, tenant-less, would otherwise 400 at the guard).
        var sessionId = $"sess_{Guid.NewGuid():N}";
        var body = JsonSerializer.Serialize(new { seq = 1, turn_id = "t1", speaker = "agent", text = "hello" });

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(body).ToUrl($"/api/conversation-sessions/{sessionId}/transcript-turns");
            x.WithRequestHeader("X-Tenant-Context", "not-a-valid-jws");
            x.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
        });
    }

    // ── Reviewer-mandated fail-closed: NON-unique session-insert failure ───────────────────────────

    /// <summary>
    /// Forces a NON-23505 session-insert failure through the real HTTP flow and asserts the fail-closed
    /// contract (Phase 3 reviewer mandate): the <c>session_insert_failed</c> audit row IS written AND no
    /// <c>conversation_sessions</c> row exists.
    ///
    /// <para>Mechanism: a value-scoped CHECK constraint is added to <c>conversation_sessions</c> that is
    /// violated ONLY by the specific <c>channel_endpoint_id</c> this test resolves to
    /// (<c>channel_endpoint_id &lt;&gt; '&lt;epId&gt;'</c> — NULL and every other endpoint id pass, so no
    /// other test is affected). The gateway's session insert (which sets that endpoint id) then fails
    /// with a 23514 check_violation — a DbUpdateException that is NOT the idempotent 23505 — exercising
    /// <c>ConversationSessionRepository.CreateAsync</c>'s catch-all detach and the handler's
    /// <c>session_insert_failed</c> audit-then-rethrow. The rethrown DbUpdateException is unmapped ⇒ 500
    /// (fail closed: no signed context returned). The constraint is dropped in a finally.</para>
    /// </summary>
    [Fact]
    public async Task SessionInsert_NonUniqueFailure_AuditsSessionInsertFailed_AndCreatesNoSession()
    {
        var tenantId = $"tr-fail-{Guid.NewGuid():N}";
        var endpointId = $"ep-failclosed-{Guid.NewGuid():N}";
        var did = $"+1555{Guid.NewGuid():N}"[..14];
        var constraintName = $"tmp_failclosed_{Guid.NewGuid():N}";

        await SeedEndpointAsync(tenantId, endpointId, TenantChannel.Voice, SignalType.PhoneNumber, did);

        await AddSessionCheckConstraintAsync(constraintName, endpointId);
        try
        {
            // The session insert violates the CHECK (23514) → non-idempotent DbUpdateException → the
            // handler audits session_insert_failed and rethrows → unmapped ⇒ 500.
            await fixture.Host.Scenario(x =>
            {
                x.Post.RawJson(TelephonyBody(did)).ToUrl(TelephonyUrl);
                x.StatusCodeShouldBe(HttpStatusCode.InternalServerError);
            });

            await using var ctx = fixture.CreateContext(tenantId);

            // (i) The session_insert_failed audit row IS written (it is this tenant's only resolution row).
            var audits = await ctx.AuditEvents.AsNoTracking()
                .Where(e => e.EventType == ResolutionEventType)
                .ToListAsync();
            var audit = Assert.Single(audits);
            AssertAuditOutcome(audit, expectedOutcome: "session_insert_failed", expectedTenantId: tenantId);

            // (ii) NO conversation_sessions row exists (the failed insert was detached + rolled back).
            var sessionCount = await ctx.ConversationSessions.AsNoTracking()
                .CountAsync(s => s.TenantId == tenantId);
            Assert.Equal(0, sessionCount);
        }
        finally
        {
            await DropSessionCheckConstraintAsync(constraintName);
        }
    }

    // ── Ambiguous-endpoint structural-prevention note ───────────────────────────────────────────────

    /// <summary>
    /// Documents why the <c>ambiguous_endpoint</c> 422 path is not provoked end-to-end here: the
    /// <c>uq_channel_signal_active</c> partial-unique index makes two ACTIVE endpoints with the same
    /// (channel, signal_type, signal_value) impossible to persist, so the resolver can never see &gt;1
    /// row while the index exists. Provoking it would require dropping that GLOBAL index, which would
    /// race the parallel <c>TenantChannelEndpointPersistenceTests</c> uniqueness test under xUnit's
    /// parallel collections. The handler's ambiguous branch + its <c>ambiguous_endpoint</c> audit are
    /// covered by the Phase 3 unit tests (mock repo returns two rows). This test asserts the structural
    /// guarantee that underpins that decision.
    /// </summary>
    [Fact]
    public async Task AmbiguousEndpoint_IsStructurallyPreventedBy_PartialUniqueIndex()
    {
        var did = $"+1555{Guid.NewGuid():N}"[..14];
        var tenantA = $"tr-amb-a-{Guid.NewGuid():N}";
        var tenantB = $"tr-amb-b-{Guid.NewGuid():N}";

        await SeedEndpointAsync(tenantA, $"ep-{Guid.NewGuid():N}", TenantChannel.Voice, SignalType.PhoneNumber, did);

        // A second ACTIVE endpoint for the same signal (different tenant) must be rejected by
        // uq_channel_signal_active (23505) — hence the resolver can never observe an ambiguous match.
        await using var ctx = fixture.CreateContext(tenantB);
        ctx.TenantChannelEndpoints.Add(new TenantChannelEndpoint
        {
            TenantId = tenantB,
            ChannelEndpointId = $"ep-{Guid.NewGuid():N}",
            Channel = TenantChannel.Voice,
            SignalType = SignalType.PhoneNumber,
            SignalValue = did,
            ConfigurationVersion = "cfg-v1",
            PolicyBundleId = "policy-001",
            Status = EndpointStatus.Active,
            Version = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        var pg = ex.InnerException as PostgresException;
        Assert.NotNull(pg);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, pg.SqlState);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Decoded projection of the ingress response body (JWS + the claim fields we assert on).</summary>
    private readonly record struct ContextClaims(
        string TenantId, string Channel, string ChannelEndpointId, string SessionId, string CorrelationId);

    private static (string Jws, ContextClaims Claims) ReadContext(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var jws = root.GetProperty("tenant_context").GetString()!;
        var c = root.GetProperty("claims");
        var claims = new ContextClaims(
            TenantId: c.GetProperty("tenant_id").GetString()!,
            Channel: c.GetProperty("channel").GetString()!,
            ChannelEndpointId: c.GetProperty("channel_endpoint_id").GetString()!,
            SessionId: c.GetProperty("session_id").GetString()!,
            CorrelationId: c.GetProperty("correlation_id").GetString()!);
        return (jws, claims);
    }

    private static async Task<AuditEvent> SingleAuditByCorrelationAsync(
        ApplicationDbContext ctx, string correlationId, bool ignoreQueryFilters = false)
    {
        var query = ctx.AuditEvents.AsNoTracking();
        if (ignoreQueryFilters)
            query = query.IgnoreQueryFilters();

        var rows = await query
            .Where(e => e.EventType == ResolutionEventType && e.CorrelationId == correlationId)
            .ToListAsync();
        return Assert.Single(rows);
    }

    private static void AssertAuditOutcome(AuditEvent audit, string expectedOutcome, string expectedTenantId)
    {
        Assert.Equal(expectedTenantId, audit.TenantId);
        using var md = JsonDocument.Parse(audit.Metadata);
        Assert.Equal(expectedOutcome, md.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(expectedTenantId, md.RootElement.GetProperty("tenant_id").GetString());
    }

    private static async Task AddSessionCheckConstraintAsync(string constraintName, string endpointId)
    {
        await using var conn = new NpgsqlConnection(TenantResolutionApiFixture.TestConnStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        // Violated (FALSE) only when channel_endpoint_id equals this test's endpoint id. NULL and every
        // other value evaluate to TRUE/NULL (not FALSE) and pass — no other test's inserts are affected.
        cmd.CommandText =
            $"ALTER TABLE public.conversation_sessions ADD CONSTRAINT \"{constraintName}\" " +
            $"CHECK (channel_endpoint_id <> '{endpointId}')";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task DropSessionCheckConstraintAsync(string constraintName)
    {
        await using var conn = new NpgsqlConnection(TenantResolutionApiFixture.TestConnStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"ALTER TABLE public.conversation_sessions DROP CONSTRAINT IF EXISTS \"{constraintName}\"";
        await cmd.ExecuteNonQueryAsync();
    }
}
