using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using Cordia.Application.Handlers.BookingOperations.SubmitConfirmedIntent;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Observability;
using Cordia.Infrastructure.Services;
using Npgsql;
using Moq;

namespace Cordia.Tests.UnitTests.Observability;

/// <summary>
/// Unit tests for the spec 13 §25.4 alert-signal counters:
///   • acceptance.idempotency_conflict_total   (Inv-3)
///   • fulfillment.capacity.invariant_violation_total (Inv-6)
///
/// (The tenant cross-scope and missing-terminal counters are asserted in
/// NormalizedStatusProjectionTests and AuditMissingTerminalNotificationTests respectively.)
/// </summary>
[Trait("Category", "Unit")]
public class AlertSignalTests : IDisposable
{
    private readonly Metrics _metrics = new();
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, long> _counters = new();

    public AlertSignalTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter == _metrics.Meter)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((inst, measurement, _, _) =>
        {
            _counters.TryGetValue(inst.Name, out var current);
            _counters[inst.Name] = current + measurement;
        });
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _metrics.Dispose();
    }

    // ── Inv-3: idempotency key reused with a DIFFERENT payload → 409 + counter ──────────────────

    [Fact]
    public async Task SubmitConfirmedIntent_DifferentPayloadForSameKey_ReturnsConflict_AndIncrementsCounter()
    {
        const string tenantId = "tenant-idem";
        const string idemKey = "idem-key-1";

        // Hasher returns a fixed hash for this request; the confirmation hash must match H.Value.
        var hasher = new Mock<ICanonicalRequestHasher>();
        hasher.Setup(h => h.Hash(It.IsAny<JsonNode?>()))
            .Returns(new CanonicalHash("canon.v1", "HASH_NEW"));

        // Existing idempotency record bound to a DIFFERENT request hash (Inv-3 conflict path).
        var idempotencyRepo = new Mock<IIdempotencyRepository>();
        idempotencyRepo.Setup(r => r.GetByKeyAsync(tenantId, idemKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdempotencyRecord
            {
                TenantId = tenantId,
                IdempotencyKey = idemKey,
                OperationId = "op-existing",
                RequestHash = "canon.v1:HASH_OLD", // different from "canon.v1:HASH_NEW"
                CreatedAt = DateTimeOffset.UtcNow,
            });

        var handler = new SubmitConfirmedIntentCommandHandler(
            hasher.Object,
            Mock.Of<IUnitOfWork>(),
            idempotencyRepo.Object,
            Mock.Of<IBookingIntentRepository>(),
            Mock.Of<IBookingOperationRepository>(),
            Mock.Of<IOutboxRepository>(),
            Mock.Of<IAuditEventRepository>(),
            _metrics);

        var command = BuildCommand(tenantId, idemKey, confirmationHash: "HASH_NEW");

        var result = await handler.Handle(command, CancellationToken.None);

        var conflict = Assert.IsType<Conflict>(result);
        Assert.Equal(idemKey, conflict.Detail.IdempotencyKey);
        Assert.Equal(1, _counters.GetValueOrDefault("acceptance.idempotency_conflict_total"));
    }

    [Fact]
    public async Task SubmitConfirmedIntent_SamePayloadReplay_DoesNotIncrementCounter()
    {
        const string tenantId = "tenant-idem";
        const string idemKey = "idem-key-2";

        var hasher = new Mock<ICanonicalRequestHasher>();
        hasher.Setup(h => h.Hash(It.IsAny<JsonNode?>()))
            .Returns(new CanonicalHash("canon.v1", "HASH_SAME"));

        // Existing record bound to the SAME hash → replay (202), not a conflict.
        var idempotencyRepo = new Mock<IIdempotencyRepository>();
        idempotencyRepo.Setup(r => r.GetByKeyAsync(tenantId, idemKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdempotencyRecord
            {
                TenantId = tenantId,
                IdempotencyKey = idemKey,
                OperationId = "op-existing",
                RequestHash = "canon.v1:HASH_SAME",
                ResponseSnapshot = null,
                CreatedAt = DateTimeOffset.UtcNow,
            });

        var handler = new SubmitConfirmedIntentCommandHandler(
            hasher.Object,
            Mock.Of<IUnitOfWork>(),
            idempotencyRepo.Object,
            Mock.Of<IBookingIntentRepository>(),
            Mock.Of<IBookingOperationRepository>(),
            Mock.Of<IOutboxRepository>(),
            Mock.Of<IAuditEventRepository>(),
            _metrics);

        var command = BuildCommand(tenantId, idemKey, confirmationHash: "HASH_SAME");

        var result = await handler.Handle(command, CancellationToken.None);

        Assert.IsType<Accepted>(result);
        Assert.Equal(0, _counters.GetValueOrDefault("acceptance.idempotency_conflict_total"));
    }

    // ── Inv-6: capacity overcommit constraint violation → counter + rethrow ─────────────────────

    [Fact]
    public void IsOvercommitViolation_RecognizesNoOvercommitConstraint()
    {
        var ex = MakeCheckViolation("ck_capacity_slots_no_overcommit");
        Assert.True(FirstPartyCapacityReservation.IsOvercommitViolation(ex));

        // Wrapped in a DbUpdateException-style outer exception is also recognised.
        var wrapped = new InvalidOperationException("update failed", ex);
        Assert.True(FirstPartyCapacityReservation.IsOvercommitViolation(wrapped));
    }

    [Fact]
    public void IsOvercommitViolation_IgnoresUnrelatedConstraints()
    {
        Assert.False(FirstPartyCapacityReservation.IsOvercommitViolation(
            MakeCheckViolation("ck_capacity_slots_nonneg")));
        Assert.False(FirstPartyCapacityReservation.IsOvercommitViolation(
            new InvalidOperationException("not a pg error")));
    }

    /// <summary>
    /// Models the catch/increment/rethrow contract used in
    /// <see cref="FirstPartyCapacityReservation.ReserveAsync"/>: a no_overcommit violation
    /// increments the Inv-6 alert signal and rethrows (the real error is never swallowed).
    /// </summary>
    [Fact]
    public void CapacityOvercommit_CatchLogic_IncrementsCounter_AndRethrows()
    {
        var violation = MakeCheckViolation("ck_capacity_slots_no_overcommit");

        void Act()
        {
            try
            {
                throw violation;
            }
            catch (Exception ex) when (FirstPartyCapacityReservation.IsOvercommitViolation(ex))
            {
                _metrics.FulfillmentCapacityInvariantViolationTotal.Add(1);
                throw;
            }
        }

        var rethrown = Assert.Throws<PostgresException>(Act);

        Assert.Same(violation, rethrown);
        Assert.Equal(1, _counters.GetValueOrDefault("fulfillment.capacity.invariant_violation_total"));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static PostgresException MakeCheckViolation(string constraintName) =>
        new(
            messageText: "check constraint violated",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.CheckViolation,
            constraintName: constraintName);

    private static SubmitConfirmedIntentCommand BuildCommand(
        string tenantId, string idempotencyKey, string confirmationHash) =>
        new(
            TenantId: tenantId,
            Request: new SubmitConfirmedIntentRequest
            {
                OperationId = "op_new",
                IdempotencyKey = idempotencyKey,
                IntentId = "intent_x",
                IntentRevision = 1,
                Revision = new IntentRevision
                {
                    IntentId = "intent_x",
                    Revision = 1,
                    SchemaVersion = "intent.v1",
                    TenantPolicyVersion = "policy.v1",
                    PayloadHash = "hash_x",
                    SourceSessionId = "sess_x",
                    CanonicalPayload = new BookingRequest
                    {
                        Operation = OperationTypeEnum.Create,
                        TargetBookingId = "",
                    },
                },
                Confirmation = new Confirmation
                {
                    ConfirmationId = "conf_x",
                    IntentId = "intent_x",
                    IntentRevision = 1,
                    IntentPayloadHash = confirmationHash,
                    Identity = "anon",
                    SourceTurnId = "turn_x",
                    ReadBackVersion = "v1",
                    VerificationLevel = "unverified",
                    ConfirmedAt = DateTimeOffset.UtcNow,
                },
            });
}
