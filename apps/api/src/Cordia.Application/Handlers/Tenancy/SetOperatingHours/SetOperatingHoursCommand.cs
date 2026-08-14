using System.Text.RegularExpressions;
using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.Tenancy.GetOperatingHours;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Tenancy.SetOperatingHours;

/// <summary>
/// Replaces the tenant's seven-day operating hours (docs/backend-gaps.md G5). Owner-gated:
/// opening hours are a commercial decision, not front-desk work. Idempotent upsert of the
/// profile row's <c>hours_json</c>; other profile fields are preserved.
/// </summary>
public sealed record SetOperatingHoursCommand(
    string TenantId,
    IReadOnlyList<OperatingHoursDay> Hours,
    string Actor,
    string ReasonCode,
    string ActorRole) : IRequest<OperatingHoursResult>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => Actor;

    public string RequiredRole => StaffRole.Owner;

    public string ActionName => "tenant.hours_set";

    public string TargetType => "tenant";

    public string TargetId => TenantId;
}

public sealed class SetOperatingHoursValidator : AbstractValidator<SetOperatingHoursCommand>
{
    private static readonly Regex HhMm = new(@"^([01]\d|2[0-3]):[0-5]\d$", RegexOptions.Compiled);

    public SetOperatingHoursValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();

        RuleFor(x => x.Hours)
            .NotNull()
            .Must(h => h.Count == 7)
            .WithMessage("hours must contain exactly 7 days.")
            .Must(h => h.Select(d => d.D).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 7)
            .WithMessage("hours must have unique days.")
            .Must(h => h.All(d => OperatingHoursDefaults.DaysOfWeek.Contains(d.D, StringComparer.OrdinalIgnoreCase)))
            .WithMessage("hours.d must be Monday through Sunday.");

        RuleForEach(x => x.Hours).ChildRules(day =>
        {
            day.RuleFor(d => d.Open)
                .Must(t => t is not null && HhMm.IsMatch(t))
                .WithMessage("open must be HH:mm in 24-hour form.");
            day.RuleFor(d => d.Close)
                .Must(t => t is not null && HhMm.IsMatch(t))
                .WithMessage("close must be HH:mm in 24-hour form.");
        });
    }
}

public sealed class SetOperatingHoursCommandHandler(
    ITenantProfileRepository profiles,
    IAuditWriter audit,
    IUnitOfWork uow)
    : IRequestHandler<SetOperatingHoursCommand, OperatingHoursResult>
{
    public async Task<OperatingHoursResult> Handle(SetOperatingHoursCommand request, CancellationToken ct)
    {
        var ordered = OperatingHoursDefaults.OrderMondayFirst(request.Hours);
        var existing = await profiles.GetAsync(request.TenantId, ct)
            ?? new TenantProfile { TenantId = request.TenantId };
        existing.HoursJson = OperatingHoursDefaults.Serialize(ordered);

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            await profiles.UpsertAsync(existing, ct2);

            await audit.WriteAsync(
                new AuditRecord(
                    TenantId: request.TenantId,
                    ActorType: StaffRole.IsKnown(request.ActorRole) ? request.ActorRole : "owner",
                    ActorId: string.IsNullOrWhiteSpace(request.Actor) ? "unknown" : request.Actor,
                    Action: "tenant.hours_set",
                    TargetType: "tenant",
                    TargetId: request.TenantId,
                    Decision: "allow",
                    DataClasses: ["tenant_profile"],
                    ReasonCode: string.IsNullOrWhiteSpace(request.ReasonCode) ? null : request.ReasonCode,
                    CorrelationId: request.TenantId),
                ct2);
        }, ct);

        return new OperatingHoursResult(ordered);
    }
}
