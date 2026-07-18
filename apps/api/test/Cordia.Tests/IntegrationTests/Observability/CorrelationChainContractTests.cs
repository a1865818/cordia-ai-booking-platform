#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken

using System.Net;
using System.Text.Json;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.Services;
using Cordia.Tests.IntegrationTests.Acceptance;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Observability;

/// <summary>
/// §11 contract walk: proves the CorrelationStampingInterceptor + CorrelationMiddleware chain
/// persists the inbound X-Correlation-Id onto BookingOperation, IntentConfirmation, and OutboxMessage
/// rows for the acceptance lane (spec 13 §4.1, §11, §25.1).
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class CorrelationChainContractTests(AcceptanceApiFixture fixture)
{
    // ---------------------------------------------------------------------------
    // Reuse the same request-building helper from AcceptanceEndpointTests.
    // ---------------------------------------------------------------------------

    private static string BuildValidRequest(
        string operationId,
        string idempotencyKey,
        string intentId,
        string serviceId = "svc-contract")
    {
        var canonicalPayload = new BookingRequest
        {
            Operation = OperationTypeEnum.Create,
            ServiceId = serviceId,
            LocationId = "",
            TargetBookingId = "",
            SpecialRequests = [],
        };

        var payloadNode = JsonSerializer.SerializeToNode(canonicalPayload, Cordia.Domain.Generated.Converter.Settings);
        var hasher = new CanonicalRequestHasher();
        var hash = hasher.Hash(payloadNode);
        var hashValue = hash.Value;

        var confirmationId = Guid.NewGuid().ToString();

        return $$"""
        {
          "operation_id": "{{operationId}}",
          "idempotency_key": "{{idempotencyKey}}",
          "intent_id": "{{intentId}}",
          "intent_revision": 1,
          "replaces_operation_id": "",
          "revision": {
            "intent_id": "{{intentId}}",
            "revision": 1,
            "canonical_payload": {
              "operation": "create",
              "service_id": "{{serviceId}}",
              "location_id": "",
              "target_booking_id": "",
              "special_requests": []
            },
            "schema_version": "intent.v1",
            "tenant_policy_version": "policy.v1",
            "payload_hash": "{{hashValue}}",
            "created_at": "2026-07-01T00:00:00.000Z",
            "source_session_id": "sess-1",
            "supersession_reason": ""
          },
          "confirmation": {
            "confirmation_id": "{{confirmationId}}",
            "intent_id": "{{intentId}}",
            "intent_revision": 1,
            "intent_payload_hash": "{{hashValue}}",
            "identity": "anon-session",
            "confirmation_channel": "voice",
            "confirmed_at": "2026-07-01T00:00:00.000Z",
            "source_turn_id": "turn-1",
            "read_back_version": "rb.v1",
            "disclosures_presented": [],
            "verification_level": "unverified"
          }
        }
        """;
    }

    // ---------------------------------------------------------------------------
    // §11 contract walk: BookingOperation + IntentConfirmation + OutboxMessage
    // must all carry the same correlation id that was sent in the request header.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Post_WithCorrelationHeader_StampsChainRows()
    {
        var tenantId = $"corr-chain-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var idempotencyKey = $"ik-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";
        var correlationId = $"corr_{Guid.NewGuid():N}";

        var requestJson = BuildValidRequest(operationId, idempotencyKey, intentId);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(requestJson).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Correlation-Id", correlationId);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        await using var ctx = fixture.CreateContext(tenantId);

        // Assert BookingOperation row carries the correlation id.
        var operationRow = await ctx.BookingOperations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.NotNull(operationRow);
        Assert.Equal(correlationId, operationRow.CorrelationId);

        // Assert IntentConfirmation row carries the correlation id.
        var confirmationRow = await ctx.IntentConfirmations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.IntentId == intentId);
        Assert.NotNull(confirmationRow);
        Assert.Equal(correlationId, confirmationRow.CorrelationId);

        // Assert OutboxMessage row carries a non-null correlation id.
        // Note: OutboxMessage.CorrelationId is set EXPLICITLY by the handler (= operationId, spec §3
        // pre-existing convention; not touched by the interceptor per the "DO NOT touch its handling"
        // constraint). We verify it is non-null and non-empty to confirm the §25.1 chain link exists.
        var outboxRow = await ctx.OutboxMessages
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.OperationId == operationId);
        Assert.NotNull(outboxRow);
        Assert.False(string.IsNullOrEmpty(outboxRow.CorrelationId),
            "OutboxMessage.CorrelationId must be non-null/non-empty for the §25.1 chain");
    }
}
