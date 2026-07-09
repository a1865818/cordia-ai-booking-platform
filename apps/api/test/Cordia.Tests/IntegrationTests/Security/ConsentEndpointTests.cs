#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Tests.IntegrationTests.Acceptance;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Security;

[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class ConsentEndpointTests(AcceptanceApiFixture fixture)
{
    private static string ConsentBody(string outcome = "granted", bool recordingAllowed = true)
        => $$"""
        {
          "consent_policy_version": "us-ca-two-party:2026-01",
          "jurisdiction_policy_id": "jp_us_ca",
          "outcome": "{{outcome}}",
          "recording_allowed": {{(recordingAllowed ? "true" : "false")}},
          "captured_at": "2026-07-09T10:00:00+00:00",
          "notice_channel": "voice"
        }
        """;

    private async Task SeedSessionAsync(string tenantId, string sessionId)
    {
        await using var db = fixture.CreateContext(tenantId);
        db.ConversationSessions.Add(new ConversationSession
        {
            TenantId = tenantId,
            SessionId = sessionId,
            Channel = TenantChannel.Voice,
            StartedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetConsentPolicy_FailsClosed_NoticeRequiredByDefault()
    {
        var tenantId = $"t14-policy-{Guid.NewGuid():N}";
        var response = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/consent-policies?channel=voice");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var doc = JsonDocument.Parse(await response.ReadAsTextAsync());
        Assert.True(doc.RootElement.GetProperty("notice_required").GetBoolean());
        Assert.False(string.IsNullOrEmpty(
            doc.RootElement.GetProperty("jurisdiction_policy_id").GetString()));
    }

    [Fact]
    public async Task GetConsentPolicy_UnknownChannel_Returns400()
    {
        var tenantId = $"t14-policy-bad-{Guid.NewGuid():N}";
        await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/consent-policies?channel=carrier-pigeon");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [Fact]
    public async Task RecordConsent_UpdatesSessionColumnsAndWritesAudit()
    {
        var tenantId = $"t14-consent-{Guid.NewGuid():N}";
        var sessionId = "sess-consent-1";
        await SeedSessionAsync(tenantId, sessionId);

        var response = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(ConsentBody()).ToUrl($"/api/conversation-sessions/{sessionId}/consent");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Correlation-Id", "corr-consent-test");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var doc = JsonDocument.Parse(await response.ReadAsTextAsync());
        Assert.Equal(sessionId, doc.RootElement.GetProperty("session_id").GetString());
        Assert.True(doc.RootElement.GetProperty("recording_allowed").GetBoolean());

        await using var db = fixture.CreateContext(tenantId);
        var session = await db.ConversationSessions.AsNoTracking()
            .SingleAsync(s => s.TenantId == tenantId && s.SessionId == sessionId);
        Assert.Equal(ConsentState.Granted, session.ConsentState);
        Assert.Equal("us-ca-two-party:2026-01", session.ConsentPolicyVersion);
        Assert.Equal("jp_us_ca", session.JurisdictionPolicyId);
        Assert.True(session.RecordingAllowed);
        Assert.NotNull(session.ConsentRecordedAt);
        Assert.Equal(1L, session.Version);

        var audit = await db.AuditEvents.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.EventType == "consent.recorded")
            .SingleAsync();
        Assert.Equal(sessionId, audit.SubjectRef);
        Assert.Equal("corr-consent-test", audit.CorrelationId);
        // jsonb normalizes formatting — parse rather than substring-match.
        using var metadata = JsonDocument.Parse(audit.Metadata);
        Assert.Equal("granted", metadata.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("agent", metadata.RootElement.GetProperty("actor_type").GetString());
        Assert.True(metadata.RootElement.GetProperty("recording_allowed").GetBoolean());
    }

    [Fact]
    public async Task RecordConsent_Rerecording_IsAllowedAndBumpsVersion()
    {
        var tenantId = $"t14-consent-re-{Guid.NewGuid():N}";
        var sessionId = "sess-consent-re";
        await SeedSessionAsync(tenantId, sessionId);

        foreach (var body in new[] { ConsentBody(), ConsentBody("declined", false) })
        {
            await fixture.Host.Scenario(x =>
            {
                x.Post.RawJson(body).ToUrl($"/api/conversation-sessions/{sessionId}/consent");
                x.WithRequestHeader("X-Tenant-Id", tenantId);
                x.StatusCodeShouldBe(HttpStatusCode.OK);
            });
        }

        await using var db = fixture.CreateContext(tenantId);
        var session = await db.ConversationSessions.AsNoTracking()
            .SingleAsync(s => s.TenantId == tenantId && s.SessionId == sessionId);
        Assert.Equal(ConsentState.Declined, session.ConsentState);
        Assert.False(session.RecordingAllowed);
        Assert.Equal(2L, session.Version);
    }

    [Fact]
    public async Task RecordConsent_CrossTenant_Returns404_NeverTouchesOtherTenantsRow()
    {
        var tenantA = $"t14-cross-a-{Guid.NewGuid():N}";
        var tenantB = $"t14-cross-b-{Guid.NewGuid():N}";
        var sessionId = "sess-cross";
        await SeedSessionAsync(tenantA, sessionId);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(ConsentBody()).ToUrl($"/api/conversation-sessions/{sessionId}/consent");
            x.WithRequestHeader("X-Tenant-Id", tenantB);
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });

        await using var db = fixture.CreateContext(tenantA);
        var session = await db.ConversationSessions.AsNoTracking()
            .SingleAsync(s => s.TenantId == tenantA && s.SessionId == sessionId);
        Assert.Null(session.ConsentState);
        Assert.Equal(0L, session.Version);
    }

    [Fact]
    public async Task RecordConsent_UnknownSession_Returns404()
    {
        var tenantId = $"t14-consent-404-{Guid.NewGuid():N}";
        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(ConsentBody()).ToUrl("/api/conversation-sessions/no-such-session/consent");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }
}
