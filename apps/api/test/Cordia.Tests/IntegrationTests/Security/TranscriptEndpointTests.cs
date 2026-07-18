#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Tests.IntegrationTests.Acceptance;

namespace Cordia.Tests.IntegrationTests.Security;

/// <summary>
/// Transcript producer endpoint (spec 14 §4.3/§5.1/§5.2):
/// POST /api/conversation-sessions/{sessionId}/transcript-turns — consent-gated fail-closed
/// (recording_allowed != true ⇒ persisted=false, nothing stored), 404 on unknown session,
/// 400 without tenant scope (Inv-13).
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class TranscriptEndpointTests(AcceptanceApiFixture fixture)
{
    private async Task SeedSessionAsync(string tenantId, string sessionId, bool? recordingAllowed)
    {
        await using var db = fixture.CreateContext(tenantId);
        db.ConversationSessions.Add(new ConversationSession
        {
            TenantId = tenantId,
            SessionId = sessionId,
            Channel = TenantChannel.Voice,
            RecordingAllowed = recordingAllowed,
            StartedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static string TurnBody(int seq, string text = "table for two at seven")
        => $$"""{"seq":{{seq}},"turn_id":"turn_{{seq}}","speaker":"customer","text":"{{text}}"}""";

    [Fact]
    public async Task ConsentedSession_PersistsTurn()
    {
        var tenantId = $"t14-tx-ok-{Guid.NewGuid():N}";
        var sessionId = "sess-tx-1";
        await SeedSessionAsync(tenantId, sessionId, recordingAllowed: true);

        var response = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(TurnBody(1)).ToUrl($"/api/conversation-sessions/{sessionId}/transcript-turns");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var doc = JsonDocument.Parse(await response.ReadAsTextAsync());
        Assert.True(doc.RootElement.GetProperty("persisted").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task NonConsentedSession_SuppressesTurn(bool? recordingAllowed)
    {
        var tenantId = $"t14-tx-no-{Guid.NewGuid():N}";
        var sessionId = "sess-tx-2";
        await SeedSessionAsync(tenantId, sessionId, recordingAllowed);

        var response = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(TurnBody(1)).ToUrl($"/api/conversation-sessions/{sessionId}/transcript-turns");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var doc = JsonDocument.Parse(await response.ReadAsTextAsync());
        Assert.False(doc.RootElement.GetProperty("persisted").GetBoolean(),
            "§5.2 fail-closed: no consent ⇒ transcript suppressed");
    }

    [Fact]
    public async Task UnknownSession_Returns404()
    {
        var tenantId = $"t14-tx-404-{Guid.NewGuid():N}";

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(TurnBody(1)).ToUrl("/api/conversation-sessions/does-not-exist/transcript-turns");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }

    [Fact]
    public async Task MissingTenant_Returns400()
    {
        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(TurnBody(1)).ToUrl("/api/conversation-sessions/sess-x/transcript-turns");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    // ── Reader (§3.1 spine + the first ConsentRequiredException call site) ────────────────────

    private async Task PostTurnAsync(string tenantId, string sessionId, int seq, string text)
        => await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(TurnBody(seq, text)).ToUrl($"/api/conversation-sessions/{sessionId}/transcript-turns");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

    [Fact]
    public async Task ProducerThenReader_RoundTrips_InOrder_WithPanMaskedAtRest_AndAllowAudit()
    {
        var tenantId = $"t14-rt-{Guid.NewGuid():N}";
        var sessionId = "sess-rt-1";
        await SeedSessionAsync(tenantId, sessionId, recordingAllowed: true);
        await PostTurnAsync(tenantId, sessionId, 2, "my card is 4111 1111 1111 1111");
        await PostTurnAsync(tenantId, sessionId, 1, "table for two");

        var response = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/conversation-sessions/{sessionId}/transcript");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "ops.jane");
            x.WithRequestHeader("X-Actor-Role", "operator");
            x.WithRequestHeader("X-Reason-Code", "dispute_review");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var bodyText = await response.ReadAsTextAsync();
        using var doc = JsonDocument.Parse(bodyText);
        var turns = doc.RootElement.GetProperty("turns").EnumerateArray().ToList();
        Assert.Equal(2, turns.Count);
        Assert.Equal(1, turns[0].GetProperty("seq").GetInt32()); // seq order, not insert order
        Assert.Equal("table for two", turns[0].GetProperty("text").GetString());
        // Inv-12: the PAN never survives to rest/read.
        Assert.DoesNotContain("4111 1111 1111 1111", bodyText);
        Assert.Contains("[REDACTED]", bodyText);

        // §5.1: the sensitive read is on the audit trail (allow). jsonb normalizes formatting —
        // parse the metadata, never substring-match it.
        await using var db = fixture.CreateContext(tenantId);
        var reads = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(db.AuditEvents)
                .Where(e => e.TenantId == tenantId && e.EventType == "read_sensitive_transcript"));
        Assert.Contains(reads, e =>
        {
            using var meta = JsonDocument.Parse(e.Metadata);
            return meta.RootElement.GetProperty("decision").GetString() == "allow";
        });
    }

    [Fact]
    public async Task Reader_WithoutCredentials_Returns403()
    {
        var tenantId = $"t14-rd403-{Guid.NewGuid():N}";
        var sessionId = "sess-rd-1";
        await SeedSessionAsync(tenantId, sessionId, recordingAllowed: true);

        await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/conversation-sessions/{sessionId}/transcript");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }

    [Fact]
    public async Task Reader_ConsentDeclined_Returns409_ConsentRequired()
    {
        var tenantId = $"t14-rd409-{Guid.NewGuid():N}";
        var sessionId = "sess-rd-2";
        await SeedSessionAsync(tenantId, sessionId, recordingAllowed: false);

        await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/conversation-sessions/{sessionId}/transcript");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "ops.jane");
            x.WithRequestHeader("X-Actor-Role", "operator");
            x.WithRequestHeader("X-Reason-Code", "dispute_review");
            x.StatusCodeShouldBe(HttpStatusCode.Conflict); // the first ConsentRequiredException thrower
        });
    }

    [Fact]
    public async Task Reader_WithValidReadSensitiveGrant_Returns200()
    {
        var tenantId = $"t14-rdgrant-{Guid.NewGuid():N}";
        var sessionId = "sess-rd-3";
        var customer = "cust-77";
        await using (var db = fixture.CreateContext(tenantId))
        {
            db.ConversationSessions.Add(new ConversationSession
            {
                TenantId = tenantId,
                SessionId = sessionId,
                Channel = TenantChannel.Voice,
                RecordingAllowed = true,
                CustomerRef = customer,
                StartedAt = DateTimeOffset.UtcNow,
            });
            db.VerificationGrants.Add(new VerificationGrant
            {
                TenantId = tenantId,
                GrantId = "grant-tx-1",
                CustomerRef = customer,
                AllowedAction = Cordia.Domain.Generated.VerificationActionEnum.ReadSensitive,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            });
            await db.SaveChangesAsync();
        }
        await PostTurnAsync(tenantId, sessionId, 1, "hello there");

        var response = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/conversation-sessions/{sessionId}/transcript");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Grant-Id", "grant-tx-1");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var doc = JsonDocument.Parse(await response.ReadAsTextAsync());
        Assert.Single(doc.RootElement.GetProperty("turns").EnumerateArray());
    }

    [Fact]
    public async Task Reader_WithExpiredGrant_Returns403()
    {
        var tenantId = $"t14-rdexp-{Guid.NewGuid():N}";
        var sessionId = "sess-rd-4";
        var customer = "cust-88";
        await using (var db = fixture.CreateContext(tenantId))
        {
            db.ConversationSessions.Add(new ConversationSession
            {
                TenantId = tenantId,
                SessionId = sessionId,
                Channel = TenantChannel.Voice,
                RecordingAllowed = true,
                CustomerRef = customer,
                StartedAt = DateTimeOffset.UtcNow,
            });
            db.VerificationGrants.Add(new VerificationGrant
            {
                TenantId = tenantId,
                GrantId = "grant-tx-2",
                CustomerRef = customer,
                AllowedAction = Cordia.Domain.Generated.VerificationActionEnum.ReadSensitive,
                IssuedAt = DateTimeOffset.UtcNow.AddHours(-2),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1), // expired
            });
            await db.SaveChangesAsync();
        }

        await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/conversation-sessions/{sessionId}/transcript");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Grant-Id", "grant-tx-2");
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }
}
