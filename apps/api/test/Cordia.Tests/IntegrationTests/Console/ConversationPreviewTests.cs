#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken

using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Tests.IntegrationTests.Console;

/// <summary>
/// G16: appending a transcript turn writes a redacted list preview; GET /api/conversation-sessions
/// returns it. Consent-suppressed turns leave preview untouched.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class ConversationPreviewTests(Acceptance.AcceptanceApiFixture fixture)
{
    [Fact]
    public async Task AppendTurn_SetsPreview_AndListReturnsIt()
    {
        var tenantId = TestIds.New("prev");
        var sessionId = "sess-preview-1";
        await SeedSessionAsync(tenantId, sessionId, recordingAllowed: true);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson("""{"seq":1,"turn_id":"turn_1","speaker":"customer","text":"table for two at seven"}""")
                .ToUrl($"/api/conversation-sessions/{sessionId}/transcript-turns");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson("""{"seq":2,"turn_id":"turn_2","speaker":"agent","text":"done, see you then"}""")
                .ToUrl($"/api/conversation-sessions/{sessionId}/transcript-turns");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var listed = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/conversation-sessions");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var doc = JsonDocument.Parse(await listed.ReadAsTextAsync());
        var session = Assert.Single(doc.RootElement.GetProperty("sessions").EnumerateArray());
        Assert.Equal(sessionId, session.GetProperty("session_id").GetString());
        Assert.Equal("done, see you then", session.GetProperty("preview").GetString());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("prov").ValueKind);
    }

    [Fact]
    public async Task SuppressedTurn_DoesNotWritePreview()
    {
        var tenantId = TestIds.New("prev-no");
        var sessionId = "sess-preview-2";
        await SeedSessionAsync(tenantId, sessionId, recordingAllowed: false);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson("""{"seq":1,"turn_id":"turn_1","speaker":"customer","text":"should not appear"}""")
                .ToUrl($"/api/conversation-sessions/{sessionId}/transcript-turns");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var listed = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/conversation-sessions");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var doc = JsonDocument.Parse(await listed.ReadAsTextAsync());
        var session = Assert.Single(doc.RootElement.GetProperty("sessions").EnumerateArray());
        Assert.True(
            session.GetProperty("preview").ValueKind is JsonValueKind.Null
            || string.IsNullOrEmpty(session.GetProperty("preview").GetString()),
            "a suppressed turn must not populate the list preview");
    }

    private async Task SeedSessionAsync(string tenantId, string sessionId, bool? recordingAllowed)
    {
        await using var db = fixture.CreateContext(tenantId);
        db.ConversationSessions.Add(new ConversationSession
        {
            TenantId = tenantId,
            SessionId = sessionId,
            Channel = TenantChannel.Web,
            RecordingAllowed = recordingAllowed,
            StartedAt = DateTimeOffset.UtcNow,
            Version = 0,
        });
        await db.SaveChangesAsync();
    }
}
