#pragma warning disable xUnit1051

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Acceptance;

/// <summary>
/// G12: who submitted a confirmed intent is stamped on the operation from <c>X-Actor</c>,
/// never from confirmation_channel.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class ProvenanceTests(AcceptanceApiFixture fixture)
{
    [Fact]
    public async Task SubmitWithoutActor_StampsAgent()
    {
        var (tenantId, operationId) = await SubmitAsync(actor: null);
        await using var ctx = fixture.CreateContext(tenantId);
        var op = await ctx.BookingOperations
            .IgnoreQueryFilters()
            .SingleAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.Equal(AttributedSource.Agent, op.AttributedSource);
    }

    [Fact]
    public async Task SubmitWithActor_StampsOperator()
    {
        var (tenantId, operationId) = await SubmitAsync(actor: "console-operator");
        await using var ctx = fixture.CreateContext(tenantId);
        var op = await ctx.BookingOperations
            .IgnoreQueryFilters()
            .SingleAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.Equal(AttributedSource.Operator, op.AttributedSource);
    }

    private async Task<(string TenantId, string OperationId)> SubmitAsync(string? actor)
    {
        var tenantId = TestIds.New("prov");
        var operationId = TestIds.New("op");
        var requestJson = BuildValidRequest(operationId, TestIds.New("ik"), TestIds.New("int"));

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(requestJson).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            if (actor is not null)
                x.WithRequestHeader("X-Actor", actor);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        return (tenantId, operationId);
    }

    private static string BuildValidRequest(string operationId, string idempotencyKey, string intentId)
    {
        var canonicalPayload = new BookingRequest
        {
            Operation = OperationTypeEnum.Create,
            ServiceId = "svc-1",
            LocationId = "",
            TargetBookingId = "",
            SpecialRequests = [],
        };
        var payloadNode = JsonSerializer.SerializeToNode(canonicalPayload, Converter.Settings);
        var hashValue = new CanonicalRequestHasher().Hash(payloadNode).Value;
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
              "service_id": "svc-1",
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
}
