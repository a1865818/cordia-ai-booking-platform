using System.Text.Json.Serialization;
using Cordia.Application.Common.Authorization;
using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Capacity.Availability;

/// <summary>
/// Reads what is bookable in a window (spec 07; docs §12 gap 10).
///
/// <para><b>Why this could not exist before.</b> `capacity_slots` had a repository read and no
/// writer at all — `AddAsync` had no caller outside tests — so the table was empty in every
/// deployment. `ReserveCapacityCommandHandler`'s tracked-path gate
/// (`slotId is not null &amp;&amp; slot row exists`) was therefore always false, no booking was
/// ever capacity-checked, and an availability read would have returned an empty list for every
/// query: a console confidently reporting "nothing available" for a venue with free capacity.
/// That is strictly worse than the honest gap it replaced.</para>
///
/// <para>The write half (<see cref="CreateCapacitySlotCommand"/>) ships with this read for
/// exactly that reason — a read over a table nothing fills is not a feature.</para>
/// </summary>
public sealed record AvailabilityQuery(
    string TenantId,
    DateTimeOffset From,
    DateTimeOffset To) : IRequest<AvailabilityResult>;

public sealed record AvailabilityResult(
    [property: JsonPropertyName("slots")] IReadOnlyList<AvailableSlot> Slots);

public sealed record AvailableSlot(
    [property: JsonPropertyName("slot_id")] string SlotId,
    [property: JsonPropertyName("resource_id")] string ResourceId,
    [property: JsonPropertyName("starts_at")] DateTimeOffset StartsAt,
    [property: JsonPropertyName("total_capacity")] int TotalCapacity,
    /// <summary>
    /// <c>total − held − booked</c>, floored at zero. This is the same arithmetic spec 07's
    /// atomic conditional write uses, so the number shown is the number that will be tested —
    /// not an approximation of it.
    /// </summary>
    [property: JsonPropertyName("free_capacity")] int FreeCapacity);

public sealed class AvailabilityValidator : AbstractValidator<AvailabilityQuery>
{
    public AvailabilityValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.To)
            .GreaterThan(x => x.From)
            .WithMessage("`to` must be after `from`.");
        RuleFor(x => x)
            .Must(q => (q.To - q.From) <= TimeSpan.FromDays(62))
            .WithMessage("The window must be 62 days or less.");
    }
}

public sealed class AvailabilityQueryHandler(ICapacitySlotRepository slots)
    : IRequestHandler<AvailabilityQuery, AvailabilityResult>
{
    public async Task<AvailabilityResult> Handle(AvailabilityQuery request, CancellationToken ct)
    {
        var rows = await slots.ListAsync(request.TenantId, request.From, request.To, ct);

        return new AvailabilityResult(
        [
            .. rows.Select(s => new AvailableSlot(
                s.SlotId,
                s.ResourceId,
                s.StartsAt,
                s.TotalCapacity,
                // Floored: a slot oversold by a race must read as 0 free, never as negative
                // capacity the console would render as a countdown.
                Math.Max(0, s.TotalCapacity - s.HeldCapacity - s.BookedCapacity))),
        ]);
    }
}

// ---------------------------------------------------------------------------
// The write half.
// ---------------------------------------------------------------------------

/// <summary>
/// Authors a bookable slot (spec 07). Owner-gated and audited: declaring capacity is a
/// commercial statement about what the venue will honour.
/// </summary>
public sealed record CreateCapacitySlotCommand(
    string TenantId,
    string? ResourceId,
    string? ServiceId,
    string? LocationId,
    DateTimeOffset StartsAt,
    int TotalCapacity,
    string Actor,
    string ReasonCode,
    string ActorRole) : IRequest<AvailableSlot>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => Actor;

    public string RequiredRole => StaffRole.Owner;

    public string ActionName => "capacity.slot_created";

    public string TargetType => "capacity_slot";

    public string TargetId => "*";
}

public sealed class CreateCapacitySlotValidator : AbstractValidator<CreateCapacitySlotCommand>
{
    public CreateCapacitySlotValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.TotalCapacity).GreaterThan(0);
        // Deliberately NO rule on Actor/ActorRole/ReasonCode. Declaring capacity is an operator
        // action, so a missing actor is an AUTHORIZATION failure, not a shape problem — and
        // `ValidationPipelineBehavior` runs BEFORE `OperatorActionBehavior`, so a NotEmpty rule
        // here answers 400 and returns before the behavior can deny-audit the attempt
        // (spec 14 §3.1 step 5). A caller probing this endpoint with no X-Actor would be turned
        // away leaving no record of having tried. The waitlist validators omit them for the same
        // reason.
        // The slot key is derived from the resource dimension; with all three absent there is
        // nothing to key on and the slot could never be matched to a booking.
        RuleFor(x => x)
            .Must(c => !string.IsNullOrWhiteSpace(c.ResourceId)
                       || !string.IsNullOrWhiteSpace(c.ServiceId)
                       || !string.IsNullOrWhiteSpace(c.LocationId))
            .WithMessage("At least one of resource_id, service_id or location_id is required.");
    }
}

public sealed class CreateCapacitySlotCommandHandler(
    ICapacitySlotRepository slots,
    IUnitOfWork uow)
    : IRequestHandler<CreateCapacitySlotCommand, AvailableSlot>
{
    public async Task<AvailableSlot> Handle(CreateCapacitySlotCommand request, CancellationToken ct)
    {
        // The SAME derivation ReserveCapacityCommandHandler uses to look a slot up. Authoring a
        // slot under any other key would create a row no booking could ever find — the table
        // would fill up and the tracked path would still never engage.
        //
        // Resolved once and reused for the stored ResourceId below, so the row's dimension and the
        // key derived from it cannot disagree.
        var resource = CapacitySlotKey.ResolveResource(
            request.ResourceId, request.ServiceId, request.LocationId);

        // CreateCapacitySlotValidator admits nothing without a non-whitespace dimension and runs
        // ahead of this handler, so null here means the two disagree about what counts as present.
        // Failing loudly beats storing a slot keyed on null that no booking could ever match.
        if (resource is null)
        {
            throw new InvalidOperationException(
                "No resource dimension on a command the validator admitted — CapacitySlotKey and "
                + "CreateCapacitySlotValidator have diverged on what counts as an empty dimension.");
        }

        var slotId = CapacitySlotKey.Derive(
            request.ResourceId, request.ServiceId, request.LocationId, request.StartsAt)!;

        var existing = await slots.GetAsync(request.TenantId, slotId, ct);
        if (existing is not null)
        {
            // Idempotent by the derived key: re-declaring the same slot returns what is there
            // rather than duplicating it or silently resetting counts that bookings depend on.
            return ToView(existing);
        }

        var slot = new CapacitySlot
        {
            TenantId = request.TenantId,
            SlotId = slotId,
            ResourceId = resource,
            StartsAt = request.StartsAt,
            TotalCapacity = request.TotalCapacity,
            HeldCapacity = 0,
            BookedCapacity = 0,
            Version = 0,
        };

        await uow.ExecuteInTransactionAsync(async ct2 => await slots.AddAsync(slot, ct2), ct);

        return ToView(slot);
    }

    private static AvailableSlot ToView(CapacitySlot s) => new(
        s.SlotId,
        s.ResourceId,
        s.StartsAt,
        s.TotalCapacity,
        Math.Max(0, s.TotalCapacity - s.HeldCapacity - s.BookedCapacity));
}
