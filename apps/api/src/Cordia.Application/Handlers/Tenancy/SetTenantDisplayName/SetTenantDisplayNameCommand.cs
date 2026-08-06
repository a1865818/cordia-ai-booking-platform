using System.Text.Json.Serialization;
using Cordia.Application.Common.Authorization;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Application.Common.Audit;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Tenancy.SetTenantDisplayName;

/// <summary>
/// Sets the tenant's guest-facing venue name (docs §12 gap 11 / §13 B2).
///
/// <para>Without a way to WRITE this, <c>tenant_display_names</c> would be a table the platform
/// only ever reads — <c>BookingReadRepository.BusinessNameAsync</c> would always return null,
/// no <c>ManageBookingCandidate</c> would ever project, and <c>GET /api/bookings?phone_e164=</c>
/// would still find nothing outside the test suite. The read and the write have to ship
/// together or the feature is not delivered.</para>
///
/// <para>Owner-gated: the venue name is what a guest is shown, so it is the owner's to set, not
/// any operator's. Idempotent by construction — one row per tenant, upserted.</para>
/// </summary>
public sealed record SetTenantDisplayNameCommand(
    string TenantId,
    string DisplayName,
    string Actor,
    string ReasonCode,
    string ActorRole) : IRequest<TenantDisplayNameResult>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => Actor;

    public string RequiredRole => StaffRole.Owner;

    public string ActionName => "tenant.display_name_set";

    public string TargetType => "tenant";

    public string TargetId => TenantId;
}

public sealed record TenantDisplayNameResult(
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("display_name")] string DisplayName);

public sealed class SetTenantDisplayNameValidator : AbstractValidator<SetTenantDisplayNameCommand>
{
    public SetTenantDisplayNameValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        // Deliberately NO rule on Actor/ActorRole/ReasonCode. This is an operator action, so a
        // missing actor is an AUTHORIZATION failure, not a shape problem — and
        // `ValidationPipelineBehavior` runs BEFORE `OperatorActionBehavior`, so a NotEmpty rule
        // here answers 400 and returns before the behavior can deny-audit the attempt
        // (spec 14 §3.1 step 5). A caller probing this endpoint with no X-Actor would be turned
        // away leaving no record of having tried. The waitlist validators omit them for the same
        // reason.

        // A guest sees this. An empty or whitespace name would render as a blank venue, and a
        // 200-character one would break every layout it appears in.
        RuleFor(x => x.DisplayName)
            .NotEmpty()
            .WithMessage("A venue name is required — it is what guests are shown.")
            .MaximumLength(200)
            .Must(n => !string.IsNullOrWhiteSpace(n))
            .WithMessage("A venue name cannot be only whitespace.");
    }
}

public sealed class SetTenantDisplayNameCommandHandler(
    ITenantDisplayNameRepository repository,
    IAuditWriter audit)
    : IRequestHandler<SetTenantDisplayNameCommand, TenantDisplayNameResult>
{
    public async Task<TenantDisplayNameResult> Handle(
        SetTenantDisplayNameCommand request, CancellationToken ct)
    {
        var trimmed = request.DisplayName.Trim();

        await repository.UpsertAsync(
            new TenantDisplayName { TenantId = request.TenantId, DisplayName = trimmed },
            ct);

        // OperatorActionBehavior writes only the DENY record; the ALLOW is the handler's job.
        // Without it this owner-only action left the audit trail able to prove who was REFUSED and
        // unable to prove who succeeded — which is the half that matters when reconstructing what
        // actually changed a venue's configuration.
        await audit.WriteAsync(
            new AuditRecord(
                TenantId: request.TenantId,
                ActorType: StaffRole.IsKnown(request.ActorRole) ? request.ActorRole : "owner",
                ActorId: string.IsNullOrWhiteSpace(request.Actor) ? "unknown" : request.Actor,
                Action: "tenant.display_name_set",
                TargetType: "tenant",
                TargetId: request.TenantId,
                Decision: "allow",
                DataClasses: ["tenant_profile"],
                ReasonCode: string.IsNullOrWhiteSpace(request.ReasonCode) ? null : request.ReasonCode,
                CorrelationId: request.TenantId),
            CancellationToken.None);

        return new TenantDisplayNameResult(request.TenantId, trimmed);
    }
}
