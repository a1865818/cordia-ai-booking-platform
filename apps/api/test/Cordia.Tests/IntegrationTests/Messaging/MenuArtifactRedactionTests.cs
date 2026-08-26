#pragma warning disable xUnit1051
using System.Text;
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

    /// <summary>
    /// Asserts the WHOLE persisted reference, not just "the PAN is gone". A masker that
    /// replaced the entire value with the token would satisfy the weaker pair of checks while
    /// destroying every upload it touched — the consumer reads this reference straight back
    /// out and hands it to the scanner and extractor, so what survives matters as much as
    /// what does not.
    /// </summary>
    [Fact]
    public async Task InlineArtifactRefCarryingPan_IsMaskedInThePersistedPayload()
    {
        var tenantId = $"artifact-pan-{Guid.NewGuid():N}";
        var messageId = await WriteMenuIngestAsync(
            tenantId,
            $"inline:Specials — paid by card {Pan} on account");

        var payload = await ReadPayloadAsync(tenantId, messageId);

        Assert.DoesNotContain(Pan, payload);
        Assert.Equal(
            $"inline:Specials — paid by card {SensitiveDataMask.MaskToken} on account",
            await ReadRefAsync(tenantId, messageId));
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
        Assert.Equal(
            $"data:text/plain,Tasting 120.00 card {SensitiveDataMask.MaskToken}",
            await ReadRefAsync(tenantId, messageId));
    }

    // ── The encoded form the first fix missed (#95 follow-up) ──────────────────────

    /// <summary>
    /// The reference that actually got through. Base64 re-alphabets the card's digits, so
    /// <c>card 4111111111111111</c> reaches this row as <c>Y2FyZCA0MTExMTExMTExMTExMTEx</c>:
    /// two ASCII digits, no 13-digit run, nothing for any detector to find. Taking
    /// <c>source_artifact_ref</c> off the exemption list made the choke point LOOK at the
    /// value; it took decoding to make it see one.
    /// </summary>
    [Fact]
    public async Task Base64ArtifactRefCarryingPan_IsMaskedInsideTheDecodedPayload()
    {
        var tenantId = $"artifact-b64-{Guid.NewGuid():N}";
        var messageId = await WriteMenuIngestAsync(
            tenantId,
            B64Ref($"Tasting menu 120.00 card {Pan} on account"));

        var decoded = DecodeB64Ref(await ReadRefAsync(tenantId, messageId));

        Assert.Equal(
            $"Tasting menu 120.00 card {SensitiveDataMask.MaskToken} on account",
            decoded);
    }

    /// <summary>
    /// The re-encode must be lossless for clean content: an ordinary base64 menu comes back
    /// byte-identical. Decoding and re-encoding every upload is the risk this fix introduces,
    /// and this is the assertion that bounds it.
    /// </summary>
    [Fact]
    public async Task Base64ArtifactRefWithOrdinaryMenu_SurvivesVerbatim()
    {
        var tenantId = $"artifact-b64-menu-{Guid.NewGuid():N}";
        var reference = B64Ref("Soup 8.50, Salad 12.00, Set menu for 2 at 65.00, table 12");

        var messageId = await WriteMenuIngestAsync(tenantId, reference);

        Assert.Equal(reference, await ReadRefAsync(tenantId, messageId));
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

    private static string B64Ref(string text) =>
        $"data:text/plain;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}";

    private static string DecodeB64Ref(string reference) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(reference.Split(',', 2)[1]));

    /// <summary>The persisted <c>source_artifact_ref</c>, JSON-decoded.</summary>
    private async Task<string> ReadRefAsync(string tenantId, string messageId) =>
        JsonDocument.Parse(await ReadPayloadAsync(tenantId, messageId))
            .RootElement.GetProperty("source_artifact_ref").GetString()!;

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
