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
}
