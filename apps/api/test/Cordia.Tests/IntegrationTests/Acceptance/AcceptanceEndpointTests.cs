#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.Idempotency;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Acceptance;

// NOTE: The API's AddControllers() now registers all custom Converter.Settings from SharedTypes.cs
// via AddJsonOptions().  Therefore ChannelEnum must be sent as its string wire value ("voice")
// not the integer ordinal.  OperationTypeEnum is handled by OperationTypeEnumConverter (reads the
// same "create" string as before).  The hash helper below round-trips a BookingRequest through
// Converter.Settings to replicate exactly what JsonSerializer.SerializeToNode(canonicalPayload,
// Converter.Settings) computes in the handler — union types (DurationMinutes, StartsAt) serialise
// back to their wire form (number / TriState string) rather than the struct layout.

/// <summary>
/// HTTP-level acceptance tests for the booking submission and status endpoints (spec 04 §12 matrix).
///
/// Tests:
///   T1  Happy path — first submission → 202, idempotency row persisted.
///   T2  Idempotent replay — same key + same payload → 202, same operation_id.
///   T3  Conflict — same key + different payload → 409.
///   T4  Hash mismatch — wrong intent_payload_hash → 422.
///   T5  Missing X-Tenant-Id header → 400.
///   T6  Structurally invalid body (missing revision) → 400.
///   T7  Atomicity / no orphan on PK collision (Inv-4).
///   T8  Status endpoint — 200 after 202, 404 unknown, 400 without tenant header.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class AcceptanceEndpointTests(AcceptanceApiFixture fixture)
{
    // ---------------------------------------------------------------------------
    // Helper: build a valid SubmitConfirmedIntentRequest JSON string.
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Builds a valid <c>POST /api/bookings</c> JSON body.
    ///
    /// The canonical hash is computed by serializing a <see cref="BookingRequest"/> through
    /// default <see cref="JsonSerializer"/> (matching what the handler does) and then passing
    /// the resulting <see cref="JsonNode"/> through <see cref="CanonicalRequestHasher.Hash"/>.
    ///
    /// <paramref name="serviceId"/> is varied between test cases to produce distinct payloads
    /// (and therefore distinct hashes) while staying within the plain-string fields that round-
    /// trip cleanly through default STJ.
    /// </summary>
    private static string BuildValidRequest(
        string operationId,
        string idempotencyKey,
        string intentId,
        string serviceId = "svc-1")
    {
        // Build the canonical payload object; serialize through Converter.Settings to mirror
        // exactly what the handler does: JsonSerializer.SerializeToNode(revision.CanonicalPayload,
        // Converter.Settings).  Union types (DurationMinutes, StartsAt) serialise back to their
        // wire form (number or TriState string) rather than the struct layout.
        // Include the same non-nullable string fields that are sent in the body so the
        // hash computation matches exactly what the handler will compute.
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

        // Confirmation: confirmation_channel sent as string wire value ("voice") because
        // ChannelEnumConverter is now registered via AddJsonOptions and reads string tokens.
        var confirmationId = Guid.NewGuid().ToString();

        // Note: non-nullable string fields in the generated C# type (BookingRequest, IntentRevision,
        // SubmitConfirmedIntentRequest) are treated as required by [ApiController] model binding.
        // We satisfy them with empty strings where the spec semantics allow absent values.
        // ReplacesOperationId, SupersessionReason, LocationId, TargetBookingId are empty strings.
        // SpecialRequests is an empty array.
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
    // T1: Happy path — first submission → 202
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Post_NewRequest_Returns202AndPersistsRows()
    {
        var tenantId = $"acc-t1-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var idempotencyKey = $"ik-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";

        var requestJson = BuildValidRequest(operationId, idempotencyKey, intentId);

        var result = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(requestJson).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        // Assert response body.
        var bodyText = await result.ReadAsTextAsync();
        var doc = JsonDocument.Parse(bodyText);
        Assert.Equal(operationId, doc.RootElement.GetProperty("operation_id").GetString());
        Assert.Equal("accepted", doc.RootElement.GetProperty("status").GetString());

        // Assert DB rows persisted.
        await using var ctx = fixture.CreateContext(tenantId);

        var idempotencyRow = await ctx.IdempotencyRecords
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.IdempotencyKey == idempotencyKey);
        Assert.NotNull(idempotencyRow);
        Assert.Equal(operationId, idempotencyRow.OperationId);

        var operationRow = await ctx.BookingOperations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.NotNull(operationRow);

        var outboxRow = await ctx.OutboxMessages
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.TenantId == tenantId && m.OperationId == operationId);
        Assert.NotNull(outboxRow);
    }

    // ---------------------------------------------------------------------------
    // T2: Idempotent replay — same key + same payload → 202, same operation_id
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Post_SameKeyAndPayload_ReturnsIdempotent202()
    {
        var tenantId = $"acc-t2-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var idempotencyKey = $"ik-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";

        var requestJson = BuildValidRequest(operationId, idempotencyKey, intentId);

        // First submission.
        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(requestJson).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        // Idempotent replay — identical body, same idempotency key.
        var replayResult = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(requestJson).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        var bodyText = await replayResult.ReadAsTextAsync();
        var doc = JsonDocument.Parse(bodyText);
        // Replayed response must carry the SAME operation_id as the first acceptance.
        Assert.Equal(operationId, doc.RootElement.GetProperty("operation_id").GetString());
        Assert.Equal("accepted", doc.RootElement.GetProperty("status").GetString());

        // Exactly one idempotency record, one operation row, and one outbox row (no duplicates).
        await using var ctx = fixture.CreateContext(tenantId);
        var idemCount = await ctx.IdempotencyRecords
            .IgnoreQueryFilters()
            .CountAsync(r => r.TenantId == tenantId && r.IdempotencyKey == idempotencyKey);
        Assert.Equal(1, idemCount);

        var opCount = await ctx.BookingOperations
            .IgnoreQueryFilters()
            .CountAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.Equal(1, opCount);

        var outboxCount = await ctx.OutboxMessages
            .IgnoreQueryFilters()
            .CountAsync(m => m.TenantId == tenantId && m.OperationId == operationId);
        Assert.Equal(1, outboxCount);
    }

    // ---------------------------------------------------------------------------
    // T3: Conflict — same idempotency key, different payload → 409
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Post_SameKeyDifferentPayload_Returns409()
    {
        var tenantId = $"acc-t3-{Guid.NewGuid():N}";
        var operationId1 = $"op-{Guid.NewGuid():N}";
        var operationId2 = $"op-{Guid.NewGuid():N}";
        var idempotencyKey = $"ik-{Guid.NewGuid():N}"; // shared key
        var intentId = $"int-{Guid.NewGuid():N}";

        // First submission — service_id "svc-1".
        var request1 = BuildValidRequest(operationId1, idempotencyKey, intentId, serviceId: "svc-1");
        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(request1).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        // Second submission — same key, different payload (service_id "svc-2").
        var request2 = BuildValidRequest(operationId2, idempotencyKey, intentId, serviceId: "svc-2");
        var conflictResult = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(request2).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Conflict);
        });

        var bodyText = await conflictResult.ReadAsTextAsync();
        var doc = JsonDocument.Parse(bodyText);
        Assert.Equal(idempotencyKey, doc.RootElement.GetProperty("idempotency_key").GetString());
        Assert.Equal(operationId1, doc.RootElement.GetProperty("existing_operation_id").GetString());
    }

    // ---------------------------------------------------------------------------
    // T4: Hash mismatch — tampered intent_payload_hash → 422
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Post_WrongHash_Returns422()
    {
        var tenantId = $"acc-t4-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var idempotencyKey = $"ik-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";
        var confirmationId = Guid.NewGuid().ToString();

        // Build a request where intent_payload_hash is deliberately wrong.
        var tamperedRequest = $$"""
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
              "service_id": "svc-hash-test",
              "location_id": "",
              "target_booking_id": "",
              "special_requests": []
            },
            "schema_version": "intent.v1",
            "tenant_policy_version": "policy.v1",
            "payload_hash": "wrong-hash-value",
            "created_at": "2026-07-01T00:00:00.000Z",
            "source_session_id": "sess-1",
            "supersession_reason": ""
          },
          "confirmation": {
            "confirmation_id": "{{confirmationId}}",
            "intent_id": "{{intentId}}",
            "intent_revision": 1,
            "intent_payload_hash": "wrong-hash-value",
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

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(tamperedRequest).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.UnprocessableEntity); // 422
        });
    }

    // ---------------------------------------------------------------------------
    // T5: Missing X-Tenant-Id header → 400
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Post_MissingTenantHeader_Returns400()
    {
        var operationId = $"op-{Guid.NewGuid():N}";
        var idempotencyKey = $"ik-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";
        var requestJson = BuildValidRequest(operationId, idempotencyKey, intentId);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(requestJson).ToUrl("/api/bookings");
            // Intentionally omit X-Tenant-Id header.
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    // ---------------------------------------------------------------------------
    // T6: Structurally invalid body (missing revision) → 400
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Post_MissingRevision_Returns400()
    {
        var tenantId = $"acc-t6-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";

        var invalidRequest = $$"""
        {
          "operation_id": "{{operationId}}",
          "idempotency_key": "ik-missing-revision",
          "intent_id": "int-missing-revision",
          "intent_revision": 1
        }
        """;

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(invalidRequest).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    // ---------------------------------------------------------------------------
    // T7: Atomicity / no orphan row on duplicate operation_id (Inv-4)
    //
    // Scenario: POST key=K1 → 202 (wins election, writes operation row OP1).
    // POST key=K2, same operation_id OP1 → the booking_operations INSERT hits the
    // (tenant_id, operation_id) PK; the transaction rolls back; the idempotency
    // row for K2 must NOT persist (Inv-4).
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Post_DuplicateOperationId_RollsBackIdempotencyRow()
    {
        var tenantId = $"acc-t7-{Guid.NewGuid():N}";
        var sharedOperationId = $"op-{Guid.NewGuid():N}"; // same op-id for both submissions
        var idempotencyKey1 = $"ik-{Guid.NewGuid():N}";
        var idempotencyKey2 = $"ik-{Guid.NewGuid():N}";
        var intentId1 = $"int-{Guid.NewGuid():N}";
        var intentId2 = $"int-{Guid.NewGuid():N}";

        // First POST: wins the election, writes all rows including booking_operations(OP1).
        var request1 = BuildValidRequest(sharedOperationId, idempotencyKey1, intentId1, serviceId: "svc-a");
        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(request1).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        // Second POST: different idempotency key (new election), but SAME operation_id.
        // booking_operations INSERT will hit the (tenant_id, operation_id) PK → transaction
        // rolls back → expect a 5xx error response.
        var request2 = BuildValidRequest(sharedOperationId, idempotencyKey2, intentId2, serviceId: "svc-b");
        var errorResponse = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(request2).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            // DB-level PK violation propagates as an unhandled exception → 500.
            x.StatusCodeShouldBe(HttpStatusCode.InternalServerError);
        });

        // Assert no orphan idempotency row for K2 — the transaction must have rolled back.
        await using var ctx = fixture.CreateContext(tenantId);
        var orphan = await ctx.IdempotencyRecords
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.IdempotencyKey == idempotencyKey2);
        Assert.Null(orphan);
    }

    // ---------------------------------------------------------------------------
    // T8: Status endpoint — 200 after 202, 404 unknown, 400 without tenant header
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task GetStatus_After202_Returns200WithAcceptedStatus()
    {
        var tenantId = $"acc-t8-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var idempotencyKey = $"ik-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";

        // Submit to create the operation.
        var requestJson = BuildValidRequest(operationId, idempotencyKey, intentId);
        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(requestJson).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        // GET status → 200 with status=accepted (NormalizedOperationStatusEnum.Accepted = 0
        // when serialized by default STJ which has no converter registered for this enum).
        var statusResult = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/booking-operations/{operationId}");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var bodyText = await statusResult.ReadAsTextAsync();
        var doc = JsonDocument.Parse(bodyText);
        Assert.Equal(operationId, doc.RootElement.GetProperty("operation_id").GetString());
        // NormalizedOperationStatusEnumConverter is now registered via AddJsonOptions, so
        // the enum serialises as its wire string "accepted" rather than integer ordinal 0.
        Assert.Equal("accepted", doc.RootElement.GetProperty("status").GetString());
        Assert.True(doc.RootElement.GetProperty("pending").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("terminal").GetBoolean());

        // GET unknown operation → 404.
        await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/booking-operations/op-does-not-exist");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });

        // GET without tenant header → 400.
        await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/booking-operations/{operationId}");
            // Intentionally omit X-Tenant-Id header.
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    // ---------------------------------------------------------------------------
    // T9: Concurrent creator election (§12 row 3 / §13.4)
    //
    // N=8 identical parallel POSTs share the same idempotency key+payload.
    // Exactly ONE election winner inserts the idempotency_records row; the seven
    // losers reload and replay. All 8 must return 202 with the SAME operation_id;
    // exactly ONE booking_operations row and ONE outbox_messages row must exist.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Post_ConcurrentIdenticalRequests_ElectOneOperation()
    {
        const int Concurrency = 8;
        var tenantId = $"acc-t9-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var idempotencyKey = $"ik-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";

        var requestJson = BuildValidRequest(operationId, idempotencyKey, intentId);

        // Fire N concurrent POSTs.
        var tasks = Enumerable.Range(0, Concurrency).Select(_ =>
            fixture.Host.Scenario(x =>
            {
                x.Post.RawJson(requestJson).ToUrl("/api/bookings");
                x.WithRequestHeader("X-Tenant-Id", tenantId);
                x.StatusCodeShouldBe(HttpStatusCode.Accepted);
            })).ToList();

        var results = await Task.WhenAll(tasks);

        // All 8 responses must return the same operation_id.
        var operationIds = results
            .Select(async r => JsonDocument.Parse(await r.ReadAsTextAsync())
                .RootElement.GetProperty("operation_id").GetString())
            .Select(t => t.Result)
            .Distinct()
            .ToList();

        Assert.Single(operationIds); // exactly one unique operation_id
        Assert.Equal(operationId, operationIds[0]);

        // Exactly one booking_operations row and one outbox_messages row.
        await using var ctx = fixture.CreateContext(tenantId);

        var opCount = await ctx.BookingOperations
            .IgnoreQueryFilters()
            .CountAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.Equal(1, opCount);

        var outboxCount = await ctx.OutboxMessages
            .IgnoreQueryFilters()
            .CountAsync(m => m.TenantId == tenantId && m.OperationId == operationId);
        Assert.Equal(1, outboxCount);

        var idemCount = await ctx.IdempotencyRecords
            .IgnoreQueryFilters()
            .CountAsync(r => r.TenantId == tenantId && r.IdempotencyKey == idempotencyKey);
        Assert.Equal(1, idemCount);
    }

    // ---------------------------------------------------------------------------
    // T10: Tenant isolation (§12 row 9 / Inv-13)
    //
    // Same idempotency key K under two different tenants A and B must produce two
    // distinct operations (one per tenant), each invisible from the other tenant's
    // context.  Proves the (tenant_id, idempotency_key) scope of the unique constraint.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Post_SameKeyDifferentTenants_ProducesTwoDistinctOperations()
    {
        var tenantA = $"acc-t10a-{Guid.NewGuid():N}";
        var tenantB = $"acc-t10b-{Guid.NewGuid():N}";
        var sharedKey = $"ik-{Guid.NewGuid():N}";

        var opIdA = $"op-{Guid.NewGuid():N}";
        var opIdB = $"op-{Guid.NewGuid():N}";
        var intentIdA = $"int-{Guid.NewGuid():N}";
        var intentIdB = $"int-{Guid.NewGuid():N}";

        var requestA = BuildValidRequest(opIdA, sharedKey, intentIdA, serviceId: "svc-tenant-a");
        var requestB = BuildValidRequest(opIdB, sharedKey, intentIdB, serviceId: "svc-tenant-b");

        // POST under tenant A.
        var resultA = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(requestA).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantA);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        // POST under tenant B — same idempotency key, different tenant.
        var resultB = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(requestB).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantB);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        var bodyA = JsonDocument.Parse(await resultA.ReadAsTextAsync());
        var bodyB = JsonDocument.Parse(await resultB.ReadAsTextAsync());

        var returnedOpIdA = bodyA.RootElement.GetProperty("operation_id").GetString();
        var returnedOpIdB = bodyB.RootElement.GetProperty("operation_id").GetString();

        // Both 202s should carry their respective operation_ids — not each other's.
        Assert.Equal(opIdA, returnedOpIdA);
        Assert.Equal(opIdB, returnedOpIdB);
        Assert.NotEqual(opIdA, opIdB); // sanity — they are different operations

        // DB: both operations exist under their respective tenants.
        await using var ctxA = fixture.CreateContext(tenantA);
        await using var ctxB = fixture.CreateContext(tenantB);

        var opA = await ctxA.BookingOperations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.TenantId == tenantA && o.OperationId == opIdA);
        Assert.NotNull(opA);

        var opB = await ctxB.BookingOperations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.TenantId == tenantB && o.OperationId == opIdB);
        Assert.NotNull(opB);

        // Cross-tenant isolation: opA is NOT visible under tenantB, and vice versa.
        var opACrossB = await ctxB.BookingOperations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.TenantId == tenantB && o.OperationId == opIdA);
        Assert.Null(opACrossB);

        var opBCrossA = await ctxA.BookingOperations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.TenantId == tenantA && o.OperationId == opIdB);
        Assert.Null(opBCrossA);
    }

    // ---------------------------------------------------------------------------
    // T11: Minimal payload — optional fields entirely omitted → 202
    //
    // Proves the SuppressImplicitRequiredAttributeForNonNullableReferenceTypes fix:
    // the real Python agent omits absent optionals (replaces_operation_id,
    // supersession_reason, location_id, target_booking_id, special_requests) rather
    // than sending empty strings / arrays.  Without the fix, [ApiController] + NRT
    // treats every non-nullable reference-type property as implicitly [Required] and
    // returns HTTP 400.  With the fix, the request binds, FluentValidation passes
    // (genuinely-required fields are present), and the handler persists the row.
    //
    // Hash note: CanonicalRequestHasher omits empty strings and empty arrays, so
    // omitting location_id/target_booking_id/special_requests hashes identically to
    // sending "" / [].  We compute the hash over a BookingRequest that only carries
    // operation + service_id, which is exactly what the Python agent sends.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Post_MinimalPayloadOmittingOptionals_Returns202()
    {
        var tenantId = $"acc-t11-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var idempotencyKey = $"ik-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";
        var confirmationId = Guid.NewGuid().ToString();

        // Minimal BookingRequest: only the required fields; omit the Pydantic-optional
        // fields (location_id, target_booking_id, special_requests).
        // The CanonicalRequestHasher drops empty strings and empty arrays, so the hash
        // over this minimal object equals the hash over the same object with ""/"[]" values.
        var minimalPayload = new BookingRequest
        {
            Operation = OperationTypeEnum.Create,
            ServiceId = "svc-minimal",
            // LocationId, TargetBookingId, SpecialRequests intentionally left at their
            // default (null / unset) — they will serialize as null and be omitted by the
            // canonicalizer, producing the same hash as if they were absent from the JSON.
        };

        // Compute the hash the same way the handler does:
        // JsonSerializer.SerializeToNode(…, Converter.Settings) → CanonicalRequestHasher.Hash.
        var payloadNode = JsonSerializer.SerializeToNode(minimalPayload, Cordia.Domain.Generated.Converter.Settings);
        var hash = new CanonicalRequestHasher().Hash(payloadNode).Value;

        // Build the request JSON with optional top-level fields omitted and optional
        // nested fields omitted from both revision.canonical_payload and the outer body.
        var minimalRequest = $$"""
        {
          "operation_id": "{{operationId}}",
          "idempotency_key": "{{idempotencyKey}}",
          "intent_id": "{{intentId}}",
          "intent_revision": 1,
          "revision": {
            "intent_id": "{{intentId}}",
            "revision": 1,
            "canonical_payload": {
              "operation": "create",
              "service_id": "svc-minimal"
            },
            "schema_version": "intent.v1",
            "tenant_policy_version": "policy.v1",
            "payload_hash": "{{hash}}",
            "created_at": "2026-07-01T00:00:00.000Z",
            "source_session_id": "sess-min"
          },
          "confirmation": {
            "confirmation_id": "{{confirmationId}}",
            "intent_id": "{{intentId}}",
            "intent_revision": 1,
            "intent_payload_hash": "{{hash}}",
            "identity": "anon-session",
            "confirmation_channel": "voice",
            "confirmed_at": "2026-07-01T00:00:00.000Z",
            "source_turn_id": "turn-min",
            "read_back_version": "rb.v1",
            "disclosures_presented": [],
            "verification_level": "unverified"
          }
        }
        """;

        var result = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(minimalRequest).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        // Assert response body.
        var bodyText = await result.ReadAsTextAsync();
        var doc = JsonDocument.Parse(bodyText);
        Assert.Equal(operationId, doc.RootElement.GetProperty("operation_id").GetString());
        Assert.Equal("accepted", doc.RootElement.GetProperty("status").GetString());

        // Assert one booking_operations row persisted.
        await using var ctx = fixture.CreateContext(tenantId);
        var operationRow = await ctx.BookingOperations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.NotNull(operationRow);
    }

    // ---------------------------------------------------------------------------
    // T12: Union-converter interop — party_size and duration_minutes as JSON numbers → 202
    //
    // Regression guard for the DurationMinutes / StartsAt union-converter bug:
    // the quicktype-generated DurationMinutes type requires a custom JsonConverter
    // that was previously absent from the MVC pipeline.  POSTing "party_size": 2
    // (a number) used to return HTTP 400 "The JSON value could not be converted to
    // Cordia.Domain.Generated.DurationMinutes".  After registering Converter.Settings
    // in AddJsonOptions the pipeline deserialises the number correctly.
    //
    // The hash is computed directly over JsonNode.Parse(rawJson) to mirror exactly
    // how the Python agent would compute it — no C# object roundtrip — proving that
    // the handler's SerializeToNode(canonicalPayload, Converter.Settings) produces
    // equivalent JSON and the hash check passes.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Post_PayloadWithNumericPartySizeAndDuration_Returns202()
    {
        var tenantId = $"acc-t12-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var idempotencyKey = $"ik-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";
        var confirmationId = Guid.NewGuid().ToString();

        // Canonical payload JSON with party_size and duration_minutes as JSON numbers.
        // This is the exact shape the Python agent sends — it used to yield HTTP 400.
        var canonicalPayloadJson = """
            {
              "operation": "create",
              "service_id": "svc-union-test",
              "party_size": 2,
              "duration_minutes": 60
            }
            """;

        // Compute the hash over the raw JSON node — same method the Python agent uses.
        var hash = new CanonicalRequestHasher().Hash(JsonNode.Parse(canonicalPayloadJson)).Value;

        var requestJson = $$"""
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
              "service_id": "svc-union-test",
              "party_size": 2,
              "duration_minutes": 60
            },
            "schema_version": "intent.v1",
            "tenant_policy_version": "policy.v1",
            "payload_hash": "{{hash}}",
            "created_at": "2026-07-01T00:00:00.000Z",
            "source_session_id": "sess-t12"
          },
          "confirmation": {
            "confirmation_id": "{{confirmationId}}",
            "intent_id": "{{intentId}}",
            "intent_revision": 1,
            "intent_payload_hash": "{{hash}}",
            "identity": "anon-session",
            "confirmation_channel": "voice",
            "confirmed_at": "2026-07-01T00:00:00.000Z",
            "source_turn_id": "turn-t12",
            "read_back_version": "rb.v1",
            "disclosures_presented": [],
            "verification_level": "unverified"
          }
        }
        """;

        var result = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(requestJson).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        // Assert response body.
        var bodyText = await result.ReadAsTextAsync();
        var doc = JsonDocument.Parse(bodyText);
        Assert.Equal(operationId, doc.RootElement.GetProperty("operation_id").GetString());
        Assert.Equal("accepted", doc.RootElement.GetProperty("status").GetString());

        // Assert one booking_operations row persisted.
        await using var ctx = fixture.CreateContext(tenantId);
        var operationRow = await ctx.BookingOperations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.NotNull(operationRow);
    }

    // ---------------------------------------------------------------------------
    // T13: Union-converter interop — duration_minutes as TriState string → 202
    //
    // Proves the string branch of DurationMinutesConverter: the Python agent may
    // send "duration_minutes": "unknown" (TriState) instead of a number.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Post_PayloadWithTriStateDurationMinutes_Returns202()
    {
        var tenantId = $"acc-t13-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var idempotencyKey = $"ik-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";
        var confirmationId = Guid.NewGuid().ToString();

        // Canonical payload with duration_minutes as the TriState string "unknown".
        var canonicalPayloadJson = """
            {
              "operation": "create",
              "service_id": "svc-tristate-test",
              "duration_minutes": "unknown"
            }
            """;

        var hash = new CanonicalRequestHasher().Hash(JsonNode.Parse(canonicalPayloadJson)).Value;

        var requestJson = $$"""
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
              "service_id": "svc-tristate-test",
              "duration_minutes": "unknown"
            },
            "schema_version": "intent.v1",
            "tenant_policy_version": "policy.v1",
            "payload_hash": "{{hash}}",
            "created_at": "2026-07-01T00:00:00.000Z",
            "source_session_id": "sess-t13"
          },
          "confirmation": {
            "confirmation_id": "{{confirmationId}}",
            "intent_id": "{{intentId}}",
            "intent_revision": 1,
            "intent_payload_hash": "{{hash}}",
            "identity": "anon-session",
            "confirmation_channel": "voice",
            "confirmed_at": "2026-07-01T00:00:00.000Z",
            "source_turn_id": "turn-t13",
            "read_back_version": "rb.v1",
            "disclosures_presented": [],
            "verification_level": "unverified"
          }
        }
        """;

        var result = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(requestJson).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        // Assert response body.
        var bodyText = await result.ReadAsTextAsync();
        var doc = JsonDocument.Parse(bodyText);
        Assert.Equal(operationId, doc.RootElement.GetProperty("operation_id").GetString());
        Assert.Equal("accepted", doc.RootElement.GetProperty("status").GetString());

        // Assert one booking_operations row persisted.
        await using var ctx = fixture.CreateContext(tenantId);
        var operationRow = await ctx.BookingOperations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.NotNull(operationRow);
    }

    // ---------------------------------------------------------------------------
    // T14: CanonicalPayload persisted in wire shape (Fix 3)
    //
    // Regression guard: BookingIntentRevision.canonical_payload must be persisted
    // using Converter.Settings (not default options), so union fields (party_size,
    // duration_minutes) are stored as bare JSON numbers/strings (the wire shape that
    // was hashed), NOT as the quicktype struct layout {"Integer":2,"Enum":null,...}.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Post_CanonicalPayloadPersistedInWireShape()
    {
        var tenantId = $"acc-t14-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var idempotencyKey = $"ik-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";
        var confirmationId = Guid.NewGuid().ToString();

        // Canonical payload with party_size and duration_minutes as JSON numbers.
        var canonicalPayloadJson = """
            {
              "operation": "create",
              "service_id": "svc-wire-shape",
              "party_size": 2,
              "duration_minutes": 60
            }
            """;

        var hash = new CanonicalRequestHasher().Hash(JsonNode.Parse(canonicalPayloadJson)).Value;

        var requestJson = $$"""
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
              "service_id": "svc-wire-shape",
              "party_size": 2,
              "duration_minutes": 60
            },
            "schema_version": "intent.v1",
            "tenant_policy_version": "policy.v1",
            "payload_hash": "{{hash}}",
            "created_at": "2026-07-01T00:00:00.000Z",
            "source_session_id": "sess-t14"
          },
          "confirmation": {
            "confirmation_id": "{{confirmationId}}",
            "intent_id": "{{intentId}}",
            "intent_revision": 1,
            "intent_payload_hash": "{{hash}}",
            "identity": "anon-session",
            "confirmation_channel": "voice",
            "confirmed_at": "2026-07-01T00:00:00.000Z",
            "source_turn_id": "turn-t14",
            "read_back_version": "rb.v1",
            "disclosures_presented": [],
            "verification_level": "unverified"
          }
        }
        """;

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(requestJson).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        // Load the persisted BookingIntentRevision and verify canonical_payload shape.
        await using var ctx = fixture.CreateContext(tenantId);
        var revision = await ctx.BookingIntentRevisions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.IntentId == intentId);
        Assert.NotNull(revision);

        var stored = revision.CanonicalPayload;
        // Must be valid JSON with a bare number for party_size (wire shape),
        // not the quicktype struct layout {"Integer":2,"Enum":null,"IsNull":false}.
        var storedNode = JsonNode.Parse(stored);
        Assert.NotNull(storedNode);
        var partySizeNode = storedNode["party_size"];
        Assert.NotNull(partySizeNode);
        // party_size must be a bare number, not a struct object.
        Assert.Equal(JsonValueKind.Number, partySizeNode.GetValue<JsonElement>().ValueKind);
        Assert.Equal(2, partySizeNode.GetValue<int>());
        // Struct fields must not appear anywhere in the stored JSON.
        Assert.DoesNotContain("IsNull", stored);
        Assert.DoesNotContain("\"Integer\"", stored);
    }

    // ---------------------------------------------------------------------------
    // T15: GetStatus response includes non-null customer_message (Fix 4)
    //
    // Regression guard: OperationStatusView.CustomerMessage must be a non-null,
    // non-empty string.  The Pydantic contract requires customer_message: str
    // (non-nullable); a null value causes the agent's parse_operation_status to
    // reject the response even when HTTP 200.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task GetStatus_Returns200WithNonNullCustomerMessage()
    {
        var tenantId = $"acc-t15-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var idempotencyKey = $"ik-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";

        // Submit to create the operation.
        var requestJson = BuildValidRequest(operationId, idempotencyKey, intentId);
        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(requestJson).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        // GET status → 200; customer_message must be present, non-null, non-empty.
        var statusResult = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/booking-operations/{operationId}");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var bodyText = await statusResult.ReadAsTextAsync();
        var doc = JsonDocument.Parse(bodyText);

        // status serialises as "accepted" (string) via NormalizedOperationStatusEnumConverter.
        Assert.Equal("accepted", doc.RootElement.GetProperty("status").GetString());

        // customer_message must exist and be non-empty.
        Assert.True(doc.RootElement.TryGetProperty("customer_message", out var cmProp),
            "Response must contain 'customer_message' property.");
        var customerMessage = cmProp.GetString();
        Assert.NotNull(customerMessage);
        Assert.NotEmpty(customerMessage);
    }
}
