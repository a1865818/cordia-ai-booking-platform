using Cordia.Application.Handlers.Capacity.CheckCandidateAvailability;
using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Services;
using FluentValidation.TestHelper;
using Microsoft.Extensions.Options;
using Moq;

namespace Cordia.Tests.UnitTests.Capacity;

/// <summary>
/// Unit tests for <see cref="CheckCandidateAvailabilityCommandHandler"/> and its validator
/// (spec 03 §4.3, spec 07 §4.3/§5).
/// </summary>
[Trait("Category", "Unit")]
public class CheckCandidateAvailabilityTests
{
    private const string TenantId = "t-avail-unit";
    private static readonly DateTimeOffset RequestedAt = new(2026, 8, 10, 19, 0, 0, TimeSpan.Zero);

    private readonly Mock<ICapacitySlotRepository> _slots = new();
    private readonly Mock<ICapacityReservation> _reservation = new();
    private readonly Mock<ICapacityPrimitiveResolver> _primitives = new();
    private readonly Mock<IUnitOfWork> _uow = new();

    public CheckCandidateAvailabilityTests()
    {
        _primitives
            .Setup(p => p.Resolve(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(CapacityPrimitive.FirstParty);
        _primitives
            .Setup(p => p.ModeFor(CapacityPrimitive.FirstParty))
            .Returns(AvailabilityModeEnum.Held);

        // Run the unit of work inline so the handler's reserve call actually executes.
        _uow
            .Setup(u => u.ExecuteInTransactionAsync(
                It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<CancellationToken, Task>, CancellationToken>((work, ct) => work(ct));
    }

    private CheckCandidateAvailabilityCommandHandler CreateHandler() =>
        new(
            _slots.Object,
            _reservation.Object,
            _primitives.Object,
            _uow.Object,
            Options.Create(new CapacityOptions { HoldTtl = TimeSpan.FromMinutes(30) }));

    // ── invalid draft → recoverable invalid_fields, never a thrown error ────────────────

    [Fact]
    public async Task Handle_DraftMissingStartsAt_ReportsInvalidFieldsAndNoCandidates()
    {
        var command = Command(Request(withStartsAt: false));

        var result = await CreateHandler().Handle(command, CancellationToken.None);

        Assert.Equal(CheckCandidateAvailabilityStatusEnum.Unavailable, result.Status);
        Assert.Contains("starts_at", result.Availability.InvalidFields);
        Assert.Empty(result.Availability.Candidates);
    }

    [Fact]
    public async Task Handle_DraftWithTriStateStartsAt_TreatsSentinelAsMissing()
    {
        var draft = Request();
        draft.StartsAt = TriStateEnum.Unknown;

        var result = await CreateHandler().Handle(Command(draft), CancellationToken.None);

        Assert.Contains("starts_at", result.Availability.InvalidFields);
    }

    // ── untracked resource → informational candidate (progress guaranteed) ─────────────

    [Fact]
    public async Task Handle_NoSlotRow_ReturnsInformationalCandidateForRequestedTime()
    {
        _slots
            .Setup(s => s.GetAsync(TenantId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CapacitySlot?)null);

        var result = await CreateHandler().Handle(Command(Request()), CancellationToken.None);

        Assert.Equal(CheckCandidateAvailabilityStatusEnum.Available, result.Status);
        Assert.Equal(AvailabilityModeEnum.Informational, result.Availability.Mode);
        Assert.Empty(result.Availability.InvalidFields);
        var candidate = Assert.Single(result.Availability.Candidates);
        Assert.Equal(RequestedAt, candidate.StartsAt);
        Assert.Equal(AvailabilityModeEnum.Informational, candidate.AvailabilityMode);
        Assert.Null(result.Availability.Hold);
    }

    // ── tracked, read-only ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_SlotWithCapacityAndNoHoldWanted_ReturnsLiveCandidateWithoutReserving()
    {
        SetupSlot(total: 20, held: 0, booked: 0);

        var result = await CreateHandler().Handle(Command(Request()), CancellationToken.None);

        Assert.Equal(CheckCandidateAvailabilityStatusEnum.Available, result.Status);
        Assert.Equal(AvailabilityModeEnum.Live, result.Availability.Mode);
        Assert.Single(result.Availability.Candidates);
        _reservation.Verify(
            r => r.ReserveAsync(It.IsAny<ReserveRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Handle_SlotExhausted_ReturnsUnavailableWithNoCandidates()
    {
        SetupSlot(total: 4, held: 2, booked: 2);

        var result = await CreateHandler().Handle(Command(Request()), CancellationToken.None);

        Assert.Equal(CheckCandidateAvailabilityStatusEnum.Unavailable, result.Status);
        Assert.Empty(result.Availability.Candidates);
        Assert.Empty(result.Availability.InvalidFields);
    }

    // ── tracked, want_hold ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_WantHold_ReservesUsingIdempotencyKeyAsOperationId()
    {
        SetupSlot(total: 20, held: 0, booked: 0);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        ReserveRequest? captured = null;
        _reservation
            .Setup(r => r.ReserveAsync(It.IsAny<ReserveRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ReserveRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(ReservationOutcome.Held(new CapacityHold
            {
                TenantId = TenantId,
                HoldId = "hold_abc",
                ExpiresAt = expiresAt,
                Status = HoldStatus.Active,
            }));

        var result = await CreateHandler().Handle(
            Command(Request(), wantHold: true, holdKey: "hold-idem-1"),
            CancellationToken.None);

        Assert.Equal(CheckCandidateAvailabilityStatusEnum.Held, result.Status);
        Assert.Equal(AvailabilityModeEnum.Held, result.Availability.Mode);
        Assert.NotNull(result.Availability.Hold);
        Assert.Equal("hold_abc", result.Availability.Hold.HoldId);
        Assert.Equal(expiresAt, result.Availability.Hold.ExpiresAt);
        Assert.Single(result.Availability.Candidates);

        Assert.NotNull(captured);
        Assert.Equal("hold-idem-1", captured.OperationId);
        Assert.Equal(
            CheckCandidateAvailabilityCommandHandler.AvailabilityWorkflowStep,
            captured.WorkflowStep);
        Assert.Equal(4, captured.RequestedCapacity);
    }

    [Fact]
    public async Task Handle_WantHold_UsesRequestedTtlOverTenantDefault()
    {
        SetupSlot(total: 20, held: 0, booked: 0);
        ReserveRequest? captured = null;
        _reservation
            .Setup(r => r.ReserveAsync(It.IsAny<ReserveRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ReserveRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(ReservationOutcome.Held(new CapacityHold { HoldId = "hold_ttl" }));

        var command = Command(Request(), wantHold: true, holdKey: "hold-idem-ttl");
        command.Request.HoldTtlSeconds = 120;

        await CreateHandler().Handle(command, CancellationToken.None);

        Assert.NotNull(captured);
        var ttl = captured.ExpiresAt - DateTimeOffset.UtcNow;
        Assert.InRange(ttl.TotalSeconds, 60, 121);
    }

    [Fact]
    public async Task Handle_WantHold_AmbiguousOutcome_ReportsHoldFailedButStillOffersCandidate()
    {
        SetupSlot(total: 20, held: 0, booked: 0);
        _reservation
            .Setup(r => r.ReserveAsync(It.IsAny<ReserveRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReservationOutcome.Ambiguous("provider-ref-1"));

        var result = await CreateHandler().Handle(
            Command(Request(), wantHold: true, holdKey: "hold-idem-amb"),
            CancellationToken.None);

        Assert.Equal(CheckCandidateAvailabilityStatusEnum.HoldFailed, result.Status);
        Assert.Equal(AvailabilityModeEnum.Live, result.Availability.Mode);
        Assert.Single(result.Availability.Candidates);
        Assert.Null(result.Availability.Hold);
    }

    [Fact]
    public async Task Handle_WantHold_NoCapacityOutcome_ReturnsUnavailable()
    {
        SetupSlot(total: 20, held: 0, booked: 0);
        _reservation
            .Setup(r => r.ReserveAsync(It.IsAny<ReserveRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ReservationOutcome.NoCapacity());

        var result = await CreateHandler().Handle(
            Command(Request(), wantHold: true, holdKey: "hold-idem-none"),
            CancellationToken.None);

        Assert.Equal(CheckCandidateAvailabilityStatusEnum.Unavailable, result.Status);
        Assert.Empty(result.Availability.Candidates);
    }

    // ── validator ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Validator_WantHoldWithoutIdempotencyKey_Fails()
    {
        var result = new CheckCandidateAvailabilityValidator()
            .TestValidate(Command(Request(), wantHold: true, holdKey: null));

        result.ShouldHaveValidationErrorFor(x => x.HoldIdempotencyKey);
    }

    [Fact]
    public void Validator_WantHoldWithIdempotencyKey_Passes()
    {
        var result = new CheckCandidateAvailabilityValidator()
            .TestValidate(Command(Request(), wantHold: true, holdKey: "hold-idem-ok"));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validator_MissingTenant_Fails()
    {
        var result = new CheckCandidateAvailabilityValidator()
            .TestValidate(Command(Request()) with { TenantId = string.Empty });

        result.ShouldHaveValidationErrorFor(x => x.TenantId);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────

    private void SetupSlot(int total, int held, int booked) =>
        _slots
            .Setup(s => s.GetAsync(TenantId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CapacitySlot
            {
                TenantId = TenantId,
                SlotId = "svc_001|slot",
                ResourceId = "svc_001",
                StartsAt = RequestedAt,
                TotalCapacity = total,
                HeldCapacity = held,
                BookedCapacity = booked,
            });

    private static BookingRequest Request(bool withStartsAt = true)
    {
        var request = new BookingRequest
        {
            Operation = OperationTypeEnum.Create,
            ServiceId = "svc_001",
            LocationId = "loc_001",
            PartySize = 4L,
            DurationMinutes = 90L,
        };

        if (withStartsAt)
            request.StartsAt = RequestedAt;

        return request;
    }

    private static CheckCandidateAvailabilityCommand Command(
        BookingRequest draftRequest,
        bool wantHold = false,
        string? holdKey = null) =>
        new(
            TenantId,
            new AvailabilityRequest
            {
                Draft = new BookingDraft
                {
                    TenantId = TenantId,
                    SessionId = "sess-avail",
                    SchemaVersion = "1.0.0",
                    TenantPolicyVersion = "policy.v1",
                    Request = draftRequest,
                },
                WantHold = wantHold,
            },
            holdKey);
}
