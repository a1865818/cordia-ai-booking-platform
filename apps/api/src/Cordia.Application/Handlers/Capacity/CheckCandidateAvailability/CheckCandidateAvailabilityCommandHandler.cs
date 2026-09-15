using Cordia.Domain.Capacity;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Services;
using MediatR;
using Microsoft.Extensions.Options;

namespace Cordia.Application.Handlers.Capacity.CheckCandidateAvailability;

/// <summary>
/// Answers <c>check_candidate_availability</c> (spec 03 §4.3) against first-party
/// inventory, optionally taking a bounded hold (spec 07 §4.3, Inv-6).
///
/// <para><b>Progress guarantee.</b> The agent router (<c>route_after_check_candidates</c>,
/// spec 02 §8.7) sends the turn back to <c>capture_fields</c> whenever the response has
/// <c>invalid_fields</c> or zero candidates, and <c>capture_fields</c> re-runs against the
/// same transcript — so a zero-candidate answer for an otherwise complete draft would spin.
/// Every branch below therefore returns at least one candidate EXCEPT the two that genuinely
/// require new customer input: a structurally unusable draft (which names the fields to
/// re-prompt) and exhausted first-party capacity for the requested slot.</para>
///
/// <para><b>Untracked resources.</b> When no <c>capacity_slots</c> row exists for the derived
/// slot key the tenant has no first-party inventory for that resource/time. This mirrors
/// <c>ReserveCapacityCommandHandler</c>'s non-tracked path: the requested time is returned as
/// an <see cref="AvailabilityModeEnum.Informational"/> candidate — advisory wording only,
/// never held/confirmed (Inv-16).</para>
/// </summary>
public sealed class CheckCandidateAvailabilityCommandHandler(
    ICapacitySlotRepository slots,
    ICapacityReservation reservation,
    ICapacityPrimitiveResolver primitives,
    IUnitOfWork uow,
    IOptions<CapacityOptions> capacityOptions)
    : IRequestHandler<CheckCandidateAvailabilityCommand, CheckCandidateAvailabilityResult>
{
    /// <summary>Workflow step that owns availability holds; keeps them distinct from the
    /// fulfillment engine's "create" hold for the same operation (spec 07 §4.4).</summary>
    internal const string AvailabilityWorkflowStep = "availability_hold";

    /// <summary>Duration reported when the draft does not carry one.</summary>
    internal const int DefaultDurationMinutes = 60;

    public async Task<CheckCandidateAvailabilityResult> Handle(
        CheckCandidateAvailabilityCommand command,
        CancellationToken ct)
    {
        var draft = command.Request.Draft.Request;

        // --- Step 1: structural draft gate → invalid_fields (recoverable, not an error). ---
        var invalidFields = CollectInvalidFields(draft);
        if (invalidFields.Count > 0)
        {
            return Result(
                CheckCandidateAvailabilityStatusEnum.Unavailable,
                AvailabilityModeEnum.Informational,
                candidates: [],
                hold: null,
                invalidFields: invalidFields);
        }

        var startsAt = draft.StartsAt!.Value.DateTime!.Value;
        var partySize = (int)draft.PartySize!.Value.Integer!.Value;
        var durationMinutes = draft.DurationMinutes?.Integer is { } minutes
            ? (int)minutes
            : DefaultDurationMinutes;

        // --- Step 2: resolve the first-party slot for this draft (spec 07 open-decision §4). ---
        var slotId = CapacitySlotKey.Derive(
            resourceId: null,
            serviceId: draft.ServiceId,
            locationId: draft.LocationId,
            startsAt: startsAt);

        var slot = slotId is null ? null : await slots.GetAsync(command.TenantId, slotId, ct);

        if (slot is null)
        {
            // Not first-party-tracked: publish the requested time as advisory only.
            return Result(
                CheckCandidateAvailabilityStatusEnum.Available,
                AvailabilityModeEnum.Informational,
                candidates: [Candidate(startsAt, durationMinutes, AvailabilityModeEnum.Informational)],
                hold: null,
                invalidFields: []);
        }

        var remaining = slot.TotalCapacity - slot.HeldCapacity - slot.BookedCapacity;
        if (remaining < partySize)
            return Unavailable();

        // --- Step 3: read-only check (want_hold=false) → live, no capacity_holds write. ---
        if (command.Request.WantHold != true)
        {
            return Result(
                CheckCandidateAvailabilityStatusEnum.Available,
                AvailabilityModeEnum.Live,
                candidates: [Candidate(startsAt, durationMinutes, AvailabilityModeEnum.Live)],
                hold: null,
                invalidFields: []);
        }

        // --- Step 4: bounded hold (spec 07 §4.3, Inv-6). ---
        // The hold idempotency key is carried as the reservation's operation id: the partial
        // unique index uq_active_hold_per_op_step plus ReserveAsync's active-hold pre-check
        // mean a repeat call with the same key returns the SAME hold instead of creating a
        // second one (Inv-5). No parallel idempotency store is introduced.
        var ttl = command.Request.HoldTtlSeconds is { } seconds
            ? TimeSpan.FromSeconds(seconds)
            : capacityOptions.Value.HoldTtl;

        ReservationOutcome outcome = ReservationOutcome.NoCapacity();
        await uow.ExecuteInTransactionAsync(
            async ct2 => outcome = await reservation.ReserveAsync(
                new ReserveRequest(
                    command.TenantId,
                    command.HoldIdempotencyKey!,
                    AvailabilityWorkflowStep,
                    slotId,
                    partySize,
                    DateTimeOffset.UtcNow + ttl,
                    CapacityProviders.FirstParty),
                ct2),
            ct);

        switch (outcome.Status)
        {
            case ReservationStatus.Held:
                var mode = primitives.ModeFor(
                    await primitives.ResolveAsync(command.TenantId, CapacityProviders.FirstParty, slotId, ct));
                return Result(
                    CheckCandidateAvailabilityStatusEnum.Held,
                    mode,
                    candidates: [Candidate(startsAt, durationMinutes, mode)],
                    hold: new HoldResult
                    {
                        HoldId = outcome.Hold!.HoldId,
                        ExpiresAt = outcome.Hold.ExpiresAt,
                    },
                    invalidFields: []);

            case ReservationStatus.NoCapacity:
                return Unavailable();

            default:
                // Ambiguous provider outcome (spec 07 §6.3, Inv-7): the slot looked free but no
                // hold is guaranteed. Report hold_failed and downgrade the wording to live.
                return Result(
                    CheckCandidateAvailabilityStatusEnum.HoldFailed,
                    AvailabilityModeEnum.Live,
                    candidates: [Candidate(startsAt, durationMinutes, AvailabilityModeEnum.Live)],
                    hold: null,
                    invalidFields: []);
        }
    }

    /// <summary>
    /// Names the draft fields that make an availability lookup impossible. A TriState
    /// sentinel (<c>absent</c>/<c>unknown</c>/<c>declined</c>) is not a usable value, so the
    /// generated union struct's typed branch must be populated.
    /// </summary>
    private static List<string> CollectInvalidFields(BookingRequest draft)
    {
        var invalid = new List<string>();

        if (draft.StartsAt?.DateTime is null)
            invalid.Add("starts_at");

        if (draft.PartySize?.Integer is not > 0)
            invalid.Add("party_size");

        if (string.IsNullOrWhiteSpace(draft.ServiceId) && string.IsNullOrWhiteSpace(draft.LocationId))
            invalid.Add("service_id");

        return invalid;
    }

    private static CheckCandidateAvailabilityResult Unavailable() =>
        Result(
            CheckCandidateAvailabilityStatusEnum.Unavailable,
            AvailabilityModeEnum.Live,
            candidates: [],
            hold: null,
            invalidFields: []);

    private static AvailabilityCandidate Candidate(
        DateTimeOffset startsAt,
        int durationMinutes,
        AvailabilityModeEnum mode) =>
        new()
        {
            StartsAt = startsAt,
            DurationMinutes = durationMinutes,
            AvailabilityMode = mode,
        };

    private static CheckCandidateAvailabilityResult Result(
        CheckCandidateAvailabilityStatusEnum status,
        AvailabilityModeEnum mode,
        List<AvailabilityCandidate> candidates,
        HoldResult? hold,
        List<string> invalidFields) =>
        new(status, new AvailabilityResult
        {
            Mode = mode,
            Candidates = candidates,
            Hold = hold!,
            InvalidFields = invalidFields,
            // Alternative-slot discovery is not implemented: the agent router
            // (spec 02 §8.7) consumes only `candidates` and `invalid_fields`, so a
            // populated `alternatives` list would be dead weight today.
            Alternatives = [],
        });
}
