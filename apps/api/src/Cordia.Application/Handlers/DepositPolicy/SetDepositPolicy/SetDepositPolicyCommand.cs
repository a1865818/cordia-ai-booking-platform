using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.DepositPolicy.GetDepositPolicy;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.DepositPolicy.SetDepositPolicy;

/// <summary>
/// Upserts the tenant deposit-policy settings row (docs/backend-gaps.md G7). Owner-gated.
/// Does not register or replace <c>IDepositPolicy</c> — that booking-time interface stays
/// independently wired.
/// </summary>
public sealed record SetDepositPolicyCommand(
    string TenantId,
    bool Require,
    string AmountPolicy,
    bool NoShowOnly,
    string RefundWindow,
    long? AmountMinor,
    string? Currency,
    string Actor,
    string ReasonCode,
    string ActorRole) : IRequest<DepositPolicyResult>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => Actor;

    public string RequiredRole => StaffRole.Owner;

    public string ActionName => "deposit_policy.set";

    public string TargetType => "deposit_policy";

    public string TargetId => TenantId;
}

public sealed class SetDepositPolicyValidator : AbstractValidator<SetDepositPolicyCommand>
{
    public SetDepositPolicyValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();

        RuleFor(x => x.AmountPolicy)
            .NotEmpty()
            .Must(p => DepositPolicyDefaults.AmountPolicies.Contains(p))
            .WithMessage("amount_policy must be per_service or flat.");

        RuleFor(x => x.RefundWindow)
            .NotEmpty()
            .WithMessage("refund_window is required.");

        RuleFor(x => x.AmountMinor)
            .NotNull()
            .When(x => string.Equals(x.AmountPolicy, DepositPolicyDefaults.AmountPolicyFlat, StringComparison.OrdinalIgnoreCase))
            .WithMessage("amount_minor is required when amount_policy is flat.");

        RuleFor(x => x.AmountMinor)
            .GreaterThanOrEqualTo(0)
            .When(x => x.AmountMinor is not null);
    }
}

public sealed class SetDepositPolicyCommandHandler(
    ITenantDepositPolicyRepository repo,
    IAuditWriter audit,
    IUnitOfWork uow)
    : IRequestHandler<SetDepositPolicyCommand, DepositPolicyResult>
{
    public async Task<DepositPolicyResult> Handle(SetDepositPolicyCommand request, CancellationToken ct)
    {
        var amountPolicy = request.AmountPolicy.Trim().ToLowerInvariant();
        var currency = string.IsNullOrWhiteSpace(request.Currency)
            ? DepositPolicyDefaults.Currency
            : request.Currency.Trim().ToUpperInvariant();

        var row = new TenantDepositPolicy
        {
            TenantId = request.TenantId,
            Require = request.Require,
            AmountPolicy = amountPolicy,
            NoShowOnly = request.NoShowOnly,
            RefundWindow = request.RefundWindow.Trim(),
            AmountMinor = request.AmountMinor,
            Currency = currency,
        };

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            await repo.UpsertAsync(row, ct2);

            await audit.WriteAsync(
                new AuditRecord(
                    TenantId: request.TenantId,
                    ActorType: StaffRole.IsKnown(request.ActorRole) ? request.ActorRole : "owner",
                    ActorId: string.IsNullOrWhiteSpace(request.Actor) ? "unknown" : request.Actor,
                    Action: "deposit_policy.set",
                    TargetType: "deposit_policy",
                    TargetId: request.TenantId,
                    Decision: "allow",
                    DataClasses: ["deposit_policy"],
                    ReasonCode: string.IsNullOrWhiteSpace(request.ReasonCode) ? null : request.ReasonCode,
                    CorrelationId: request.TenantId),
                ct2);
        }, ct);

        return DepositPolicyDefaults.From(row);
    }
}
