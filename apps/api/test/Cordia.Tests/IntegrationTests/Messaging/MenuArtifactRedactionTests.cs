#pragma warning disable xUnit1051
using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Security;
using Cordia.Infrastructure.Repositories;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Messaging;

/// <summary>
/// End-to-end pin for #95: a content-bearing <c>source_artifact_ref</c> must not reach
/// <c>outbox_messages.payload</c> unmasked.
///
/// <para><b>Why an integration test and not just a corpus vector.</b> The shared corpus already
/// pins that <c>SanitizeJson</c> masks the key. What it cannot show is that the masking actually
/// happens on the write path production uses: <c>OutboxRepository.AddAsync</c> is the Inv-12 choke
/// point, and before this change it ran, saw <c>source_artifact_ref</c> on the control-plane
/// exemption list, and returned the value verbatim. The choke point was executing and doing
/// nothing. These tests assert against the row as persisted.</para>
///
/// <para>The payload shape mirrors <c>UploadMenuCommandHandler</c> exactly
/// (<c>{catalog_id, source_artifact_ref}</c>), so a change to that handler's envelope shows up
/// here rather than silently bypassing the guard.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class MenuArtifactRedactionTests(PostgresFixture fixture)
{
    private const string Pan = "4111111111111111";

    // ── The fix: a PAN inside an inline: artifact ref is masked at rest ─────────────

    [Fact]
    public async Task InlineArtifactRefCarryingPan_IsMaskedInThePersistedPayload()
    {
        var tenantId = $"artifact-pan-{Guid.NewGuid():N}";
        var messageId = await WriteMenuIngestAsync(
            tenantId,
            $"inline:Specials — paid by card {Pan} on account");

        var payload = await ReadPayloadAsync(tenantId, messageId);

        Assert.DoesNotContain(Pan, payload);
        Assert.Contains(SensitiveDataMask.MaskToken, payload);
    }

    [Fact]
    public async Task DataUrlArtifactRefCarryingPan_IsMaskedInThePersistedPayload()
    {
        var tenantId = $"artifact-data-{Guid.NewGuid():N}";
        var messageId = await WriteMenuIngestAsync(
            tenantId,
            $"data:text/plain,Tasting 120.00 card {Pan}");

        var payload = await ReadPayloadAsync(tenantId, messageId);

        Assert.DoesNotContain(Pan, payload);
        Assert.Contains(SensitiveDataMask.MaskToken, payload);
    }

    // ── The cost of the fix, bounded: ordinary menu content must survive ────────────

    /// <summary>
    /// The consumer reads <c>source_artifact_ref</c> straight back out
    /// (<c>IngestVendorMenuCommandHandler</c> hands it to the scanner and extractor), so masking
    /// that mangled ordinary content would break menu ingestion rather than secure it. Masking
    /// replaces only <i>detected</i> spans, and a price list contains no Luhn-valid 13–19 digit
    /// run — but that is a claim about the detector, and this asserts it rather than assuming it.
    /// </summary>
    [Fact]
    public async Task ArtifactRefWithOrdinaryMenuPrices_SurvivesVerbatim()
    {
        var tenantId = $"artifact-menu-{Guid.NewGuid():N}";
        const string menu = "inline:Lunch 18.50, Dinner 32.00, Set menu for 2 at 65.00, table 12";

        var messageId = await WriteMenuIngestAsync(tenantId, menu);
        var payload = await ReadPayloadAsync(tenantId, messageId);

        var roundTripped = JsonDocument.Parse(payload)
            .RootElement.GetProperty("source_artifact_ref").GetString();

        Assert.Equal(menu, roundTripped);
        Assert.DoesNotContain(SensitiveDataMask.MaskToken, payload);
    }

    // ── Regression: exactly ONE key left the allowlist ─────────────────────────────

    /// <summary>
    /// <c>provider_ref</c> stays exempt on purpose — masking a Luhn-coincidental provider
    /// reference would break the §16.2 reconciliation joins that look up by it. This fails if
    /// anyone "cleans up" the exemption list by removing the mechanism instead of the one entry.
    /// </summary>
    [Fact]
    public async Task ProviderRef_RemainsExempt_AndRoundTripsVerbatim()
    {
        var tenantId = $"artifact-provider-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await new OutboxRepository(ctx).AddAsync(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = messageId,
                MessageType = "booking.command.requested.v1",
                CorrelationId = $"corr-{Guid.NewGuid():N}",
                Payload = JsonSerializer.Serialize(new { provider_ref = Pan }),
            });
        }

        var payload = await ReadPayloadAsync(tenantId, messageId);

        Assert.Contains(Pan, payload);
        Assert.DoesNotContain(SensitiveDataMask.MaskToken, payload);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────

    /// <summary>Writes the exact envelope shape <c>UploadMenuCommandHandler</c> emits.</summary>
    private async Task<string> WriteMenuIngestAsync(string tenantId, string sourceArtifactRef)
    {
        var messageId = $"msg-{Guid.NewGuid():N}";

        await using var ctx = fixture.CreateContext(tenantId);
        await new OutboxRepository(ctx).AddAsync(new OutboxMessage
        {
            TenantId = tenantId,
            MessageId = messageId,
            MessageType = "menu.commands.ingest.v1",
            CorrelationId = $"corr-{Guid.NewGuid():N}",
            Payload = JsonSerializer.Serialize(new
            {
                catalog_id = $"cat_{Guid.NewGuid():N}",
                source_artifact_ref = sourceArtifactRef,
            }),
        });

        return messageId;
    }

    private async Task<string> ReadPayloadAsync(string tenantId, string messageId)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        var row = await ctx.OutboxMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstAsync(m => m.TenantId == tenantId && m.MessageId == messageId);

        return row.Payload;
    }
}
