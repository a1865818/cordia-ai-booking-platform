using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.Tenancy.GetTenantProfile;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Tenancy.SetTenantProfile;

/// <summary>
/// Partial update of the tenant business profile (docs/backend-gaps.md G4 / G10).
///
/// Owner-gated: business type names every schedule lane, and the venue waitlist auto-book
/// switch is a commercial policy, not front-desk work. Idempotent — one row per tenant, upserted.
/// Does not write agent guardrails; G5 stays consistent the other way (putting guardrails g5
/// upserts <c>waitlist_auto_book</c>).
/// </summary>
public sealed record SetTenantProfileCommand(
    string TenantId,
    string? BusinessType,
    bool? WaitlistAutoBook,
    string Actor,
    string ReasonCode,
    string ActorRole) : IRequest<TenantProfileResult>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => Actor;

    public string RequiredRole => StaffRole.Owner;

    public string ActionName => "tenant.profile_set";

    public string TargetType => "tenant";

    public string TargetId => TenantId;
}

public static class TenantBusinessTypes
{
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "salon", "restaurant", "medical", "fitness", "cleaning", "lawn", "repair", "other",
    };
}

public sealed class SetTenantProfileValidator : AbstractValidator<SetTenantProfileCommand>
{
    public SetTenantProfileValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        // Deliberately NO rule on Actor/ActorRole/ReasonCode — operator-action identity is an
        // authorization failure (403), not a shape problem. ValidationPipelineBehavior runs
        // BEFORE OperatorActionBehavior (same rationale as SetTenantDisplayName).

        RuleFor(x => x.BusinessType)
            .Must(t => t is null || TenantBusinessTypes.Allowed.Contains(t.Trim()))
            .WithMessage("business_type must be one of salon, restaurant, medical, fitness, cleaning, lawn, repair, other.");
    }
}

public sealed class SetTenantProfileCommandHandler(
    ITenantProfileRepository profiles,
    ITenantDisplayNameRepository displayNames,
    IAuditWriter audit,
    IUnitOfWork uow)
    : IRequestHandler<SetTenantProfileCommand, TenantProfileResult>
{
    public async Task<TenantProfileResult> Handle(SetTenantProfileCommand request, CancellationToken ct)
    {
        var existing = await profiles.GetAsync(request.TenantId, ct)
            ?? new TenantProfile { TenantId = request.TenantId };

        if (request.BusinessType is not null)
            existing.BusinessType = string.IsNullOrWhiteSpace(request.BusinessType)
                ? null
                : request.BusinessType.Trim().ToLowerInvariant();

        if (request.WaitlistAutoBook is { } autoBook)
            existing.WaitlistAutoBook = autoBook;

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            await profiles.UpsertAsync(existing, ct2);

            await audit.WriteAsync(
                new AuditRecord(
                    TenantId: request.TenantId,
                    ActorType: StaffRole.IsKnown(request.ActorRole) ? request.ActorRole : "owner",
                    ActorId: string.IsNullOrWhiteSpace(request.Actor) ? "unknown" : request.Actor,
                    Action: "tenant.profile_set",
                    TargetType: "tenant",
                    TargetId: request.TenantId,
                    Decision: "allow",
                    DataClasses: ["tenant_profile"],
                    ReasonCode: string.IsNullOrWhiteSpace(request.ReasonCode) ? null : request.ReasonCode,
                    CorrelationId: request.TenantId),
                ct2);
        }, ct);

        var displayName = await displayNames.GetAsync(request.TenantId, ct) ?? string.Empty;
        return new TenantProfileResult(
            request.TenantId,
            displayName,
            existing.BusinessType,
            existing.WaitlistAutoBook);
    }
}
