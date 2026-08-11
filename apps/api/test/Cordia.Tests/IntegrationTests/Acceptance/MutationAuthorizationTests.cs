#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.Services;

namespace Cordia.Tests.IntegrationTests.Acceptance;

/// <summary>
/// Authorization on the acceptance front door for mutations of an EXISTING booking
/// (spec 08 §12: "every mutation accept requires a valid verification_grant — or an
/// authenticated owner role — scoped to (tenant, customer, target booking, action, expiry)";
/// Inv-11).
///
/// <para>Why this door and not <c>POST /api/Bookings/{id}/modifications</c>: that one consumes a
/// grant but its body (<c>SubmitModificationInput</c>) is <c>additionalProperties: false</c> with
/// no field for the changed data, so it cannot transport a new time. A reschedule has to come
/// through here — and until this suite existed, here required nothing at all. While the fulfilment
/// engine refused every modify that was academic; once it could actually move a booking, anyone
/// who knew a booking id could move someone else's.</para>
///
/// <para>A create is deliberately untouched: there is no existing booking to authorize against,
/// and the agent lane opens every new booking through this same door.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class MutationAuthorizationTests(AcceptanceApiFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Builds a <c>POST /api/bookings</c> body for the given operation, hashing the payload the
    /// same way the handler does (re-serialize the generated type through Converter.Settings).
    /// </summary>
    private static string BuildRequest(
        OperationTypeEnum operation,
        string targetBookingId,
        string operationId,
        string idempotencyKey,
        string intentId,
        string identity = "anon-session",
        string serviceId = "svc-auth")
    {
        var canonicalPayload = new BookingRequest
        {
            Operation = operation,
            ServiceId = serviceId,
            LocationId = "",
            TargetBookingId = targetBookingId,
            SpecialRequests = [],
        };

        var payloadNode = JsonSerializer.SerializeToNode(canonicalPayload, Converter.Settings);
        var hash = new CanonicalRequestHasher().Hash(payloadNode).Value;
        var operationWire = operation switch
        {
            OperationTypeEnum.Reschedule => "reschedule",
            OperationTypeEnum.Cancel => "cancel",
            _ => "create",
        };

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
              "operation": "{{operationWire}}",
              "service_id": "{{serviceId}}",
              "location_id": "",
              "target_booking_id": "{{targetBookingId}}",
              "special_requests": []
            },
            "schema_version": "intent.v1",
            "tenant_policy_version": "policy.v1",
            "payload_hash": "{{hash}}",
            "created_at": "2026-09-01T00:00:00.000Z",
            "source_session_id": "sess-auth",
            "supersession_reason": ""
          },
          "confirmation": {
            "confirmation_id": "{{Guid.NewGuid()}}",
            "intent_id": "{{intentId}}",
            "intent_revision": 1,
            "intent_payload_hash": "{{hash}}",
            "identity": "{{identity}}",
            "confirmation_channel": "web",
            "confirmed_at": "2026-09-01T00:00:00.000Z",
            "source_turn_id": "turn-auth",
            "read_back_version": "rb.v1",
            "disclosures_presented": [],
            "verification_level": "unverified"
          }
        }
        """;
    }

    private async Task<string> SeedBookingAsync(string tenantId, string customerId)
    {
        var bookingId = $"bk-{Guid.NewGuid():N}";
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.Bookings.Add(new Booking
        {
            TenantId = tenantId,
            BookingId = bookingId,
            BookingReference = $"REF{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
            LifecycleStatus = BookingLifecycleStatus.Confirmed,
            CustomerId = customerId,
            Version = 1,
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        await ctx.SaveChangesAsync();
        return bookingId;
    }

    private async Task<string> SeedGrantAsync(
        string tenantId,
        string customerId,
        string bookingId,
        VerificationActionEnum action = VerificationActionEnum.Modify,
        DateTimeOffset? expiresAt = null)
    {
        var grantId = $"grant_{Guid.NewGuid():N}";
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.VerificationGrants.Add(new VerificationGrant
        {
            TenantId = tenantId,
            GrantId = grantId,
            CustomerRef = customerId,
            AllowedAction = action,
            TargetBookingId = bookingId,
            VerificationLevel = VerificationLevel.IdentityVerified,
            IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(15),
        });
        await ctx.SaveChangesAsync();
        return grantId;
    }

    private Task<IScenarioResult> PostAsync(
        string tenantId,
        string body,
        HttpStatusCode expected,
        string? actor = null,
        string? actorRole = null,
        string? grantId = null)
        => fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(body).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            if (actor is not null) x.WithRequestHeader("X-Actor", actor);
            if (actorRole is not null) x.WithRequestHeader("X-Actor-Role", actorRole);
            if (grantId is not null) x.WithRequestHeader("X-Grant-Id", grantId);
            x.StatusCodeShouldBe(expected);
        });

    private static string Ids() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// The core refusal: knowing a booking id is not authorization to move it.
    /// </summary>
    [Fact]
    public async Task ARescheduleWithNeitherGrantNorStaffRoleIsRefused()
    {
        var tenantId = TestIds.New("mut-authz-none");
        var bookingId = await SeedBookingAsync(tenantId, "cust-1");

        await PostAsync(
            tenantId,
            BuildRequest(OperationTypeEnum.Reschedule, bookingId, $"op-{Ids()}", $"ik-{Ids()}", $"int-{Ids()}"),
            HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The console's path: an operator is authenticated staff, so no guest step-up is required.
    /// Operator (not owner) suffices — moving a booking is front-desk work, not an owner-only
    /// commercial decision (GD-1 role matrix).
    /// </summary>
    [Fact]
    public async Task AnOperatorMayReschedule()
    {
        var tenantId = TestIds.New("mut-authz-op");
        var bookingId = await SeedBookingAsync(tenantId, "cust-1");

        await PostAsync(
            tenantId,
            BuildRequest(OperationTypeEnum.Reschedule, bookingId, $"op-{Ids()}", $"ik-{Ids()}", $"int-{Ids()}"),
            HttpStatusCode.Accepted,
            actor: "staff@example.com",
            actorRole: "operator");
    }

    /// <summary>
    /// The agent lane's path: it holds a platform-issued grant and already carries the id on
    /// <c>confirmation.identity</c>, which is where the assurance derivation reads it from too.
    /// </summary>
    [Fact]
    public async Task AGrantOnTheConfirmationIdentityAuthorizes()
    {
        var tenantId = TestIds.New("mut-authz-grant");
        var bookingId = await SeedBookingAsync(tenantId, "cust-1");
        var grantId = await SeedGrantAsync(tenantId, "cust-1", bookingId);

        await PostAsync(
            tenantId,
            BuildRequest(
                OperationTypeEnum.Reschedule, bookingId, $"op-{Ids()}", $"ik-{Ids()}", $"int-{Ids()}",
                identity: grantId),
            HttpStatusCode.Accepted);
    }

    /// <summary>The same grant presented the other supported way — the X-Grant-Id header.</summary>
    [Fact]
    public async Task AGrantOnTheHeaderAuthorizes()
    {
        var tenantId = TestIds.New("mut-authz-hdr");
        var bookingId = await SeedBookingAsync(tenantId, "cust-1");
        var grantId = await SeedGrantAsync(tenantId, "cust-1", bookingId);

        await PostAsync(
            tenantId,
            BuildRequest(OperationTypeEnum.Reschedule, bookingId, $"op-{Ids()}", $"ik-{Ids()}", $"int-{Ids()}"),
            HttpStatusCode.Accepted,
            grantId: grantId);
    }

    /// <summary>
    /// Scope is the whole point of a grant: one issued for a DIFFERENT booking must not move this
    /// one. Without this the grant check would be a presence check.
    /// </summary>
    [Fact]
    public async Task AGrantScopedToAnotherBookingIsRefused()
    {
        var tenantId = TestIds.New("mut-authz-scope");
        var target = await SeedBookingAsync(tenantId, "cust-1");
        var other = await SeedBookingAsync(tenantId, "cust-1");
        var grantId = await SeedGrantAsync(tenantId, "cust-1", other);

        await PostAsync(
            tenantId,
            BuildRequest(OperationTypeEnum.Reschedule, target, $"op-{Ids()}", $"ik-{Ids()}", $"int-{Ids()}"),
            HttpStatusCode.Forbidden,
            grantId: grantId);
    }

    /// <summary>An expired grant is not a grant (spec 08 §9.3 — grants are expiring).</summary>
    [Fact]
    public async Task AnExpiredGrantIsRefused()
    {
        var tenantId = TestIds.New("mut-authz-exp");
        var bookingId = await SeedBookingAsync(tenantId, "cust-1");
        var grantId = await SeedGrantAsync(
            tenantId, "cust-1", bookingId, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        await PostAsync(
            tenantId,
            BuildRequest(OperationTypeEnum.Reschedule, bookingId, $"op-{Ids()}", $"ik-{Ids()}", $"int-{Ids()}"),
            HttpStatusCode.Forbidden,
            grantId: grantId);
    }

    /// <summary>
    /// A cancel grant does not authorize a move. Reschedule maps to MODIFY (spec 08 §9.3), and an
    /// action-scoped grant that ignored the action would be scoped in name only.
    /// </summary>
    [Fact]
    public async Task AGrantForADifferentActionIsRefused()
    {
        var tenantId = TestIds.New("mut-authz-act");
        var bookingId = await SeedBookingAsync(tenantId, "cust-1");
        var grantId = await SeedGrantAsync(
            tenantId, "cust-1", bookingId, action: VerificationActionEnum.Cancel);

        await PostAsync(
            tenantId,
            BuildRequest(OperationTypeEnum.Reschedule, bookingId, $"op-{Ids()}", $"ik-{Ids()}", $"int-{Ids()}"),
            HttpStatusCode.Forbidden,
            grantId: grantId);
    }

    /// <summary>
    /// The refusal must not be escapable by replaying a key someone else's authorized request
    /// already used: the check runs BEFORE the idempotency pre-check, so a replay is refused too.
    /// </summary>
    [Fact]
    public async Task AnUnauthorizedReplayOfAnAcceptedKeyIsAlsoRefused()
    {
        var tenantId = TestIds.New("mut-authz-replay");
        var bookingId = await SeedBookingAsync(tenantId, "cust-1");
        var opId = $"op-{Ids()}";
        var idemKey = $"ik-{Ids()}";
        var intentId = $"int-{Ids()}";
        var body = BuildRequest(OperationTypeEnum.Reschedule, bookingId, opId, idemKey, intentId);

        await PostAsync(tenantId, body, HttpStatusCode.Accepted, actor: "staff@example.com", actorRole: "operator");

        // Same body, same key — but no identity behind it this time.
        await PostAsync(tenantId, body, HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A create carries no target, so there is nothing to authorize against — the agent lane opens
    /// every new booking through this door with no grant and must keep working.
    /// </summary>
    [Fact]
    public async Task ACreateStillNeedsNoAuthorization()
    {
        var tenantId = TestIds.New("mut-authz-create");

        await PostAsync(
            tenantId,
            BuildRequest(OperationTypeEnum.Create, "", $"op-{Ids()}", $"ik-{Ids()}", $"int-{Ids()}"),
            HttpStatusCode.Accepted);
    }

    /// <summary>
    /// Tenancy is part of the scope (Inv-13): a grant issued in another tenant must not authorize
    /// here even if every other field lines up.
    /// </summary>
    [Fact]
    public async Task AGrantFromAnotherTenantIsRefused()
    {
        var tenantA = TestIds.New("mut-authz-xta");
        var tenantB = TestIds.New("mut-authz-xtb");
        var bookingId = await SeedBookingAsync(tenantA, "cust-1");
        // Same customer, same booking id — issued in the WRONG tenant.
        var foreignGrant = await SeedGrantAsync(tenantB, "cust-1", bookingId);

        await PostAsync(
            tenantA,
            BuildRequest(OperationTypeEnum.Reschedule, bookingId, $"op-{Ids()}", $"ik-{Ids()}", $"int-{Ids()}"),
            HttpStatusCode.Forbidden,
            grantId: foreignGrant);
    }
    /// <summary>
    /// A staff role authorizes a MOVE, not a CANCEL. Cancel already has a door that carries
    /// everything it needs (<c>POST /api/Bookings/{id}/cancellations</c>) and that door consumes a
    /// guest grant with no role alternative — so accepting a role here would make this endpoint the
    /// weaker way to perform the identical operation. Reschedule has no such alternative, which is
    /// the whole reason the role path exists.
    /// </summary>
    [Fact]
    public async Task AStaffRoleDoesNotAuthorizeACancelThroughThisDoor()
    {
        var tenantId = TestIds.New("mut-authz-cxl");
        var bookingId = await SeedBookingAsync(tenantId, "cust-1");

        await PostAsync(
            tenantId,
            BuildRequest(OperationTypeEnum.Cancel, bookingId, $"op-{Ids()}", $"ik-{Ids()}", $"int-{Ids()}"),
            HttpStatusCode.Forbidden,
            actor: "staff@example.com",
            actorRole: "owner");
    }

    /// <summary>The grant path still works for a cancel through this door.</summary>
    [Fact]
    public async Task ACancelGrantAuthorizesACancelThroughThisDoor()
    {
        var tenantId = TestIds.New("mut-authz-cxl-ok");
        var bookingId = await SeedBookingAsync(tenantId, "cust-1");
        var grantId = await SeedGrantAsync(
            tenantId, "cust-1", bookingId, action: VerificationActionEnum.Cancel);

        await PostAsync(
            tenantId,
            BuildRequest(OperationTypeEnum.Cancel, bookingId, $"op-{Ids()}", $"ik-{Ids()}", $"int-{Ids()}"),
            HttpStatusCode.Accepted,
            grantId: grantId);
    }

}
