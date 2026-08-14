using System.Text.Json.Serialization;
using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Deposits.RefundDeposit;

/// <summary>
/// Operator refund of a captured deposit (docs/backend-gaps.md G2). Records Paid → Refunded
/// locally; there is no Stripe call (spec 09 refunds are dashboard / this console action).
/// Unknown id → null (controller 404). Any status other than Paid → 409.
/// </summary>
public sealed record RefundDepositCommand(
    string TenantId,
    string DepositSessionId,
    string Actor,
    string ReasonCode,
    string ActorRole) : IRequest<RefundDepositResult?>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => Actor;

    public string RequiredRole => StaffRole.Operator;

    public string ActionName => "deposit.refunded";

    public string TargetType => "deposit_session";

    public string TargetId => DepositSessionId;
}

public sealed record RefundDepositResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status);

public sealed class RefundDepositValidator : AbstractValidator<RefundDepositCommand>
{
    public RefundDepositValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.DepositSessionId).NotEmpty();
    }
}

public sealed class RefundDepositCommandHandler(
    IDepositSessionRepository repo,
    IAuditWriter audit,
    IUnitOfWork uow)
    : IRequestHandler<RefundDepositCommand, RefundDepositResult?>
{
    public async Task<RefundDepositResult?> Handle(RefundDepositCommand request, CancellationToken ct)
    {
        var session = await repo.GetAsync(request.TenantId, request.DepositSessionId, ct);
        if (session is null)
            return null;

        if (session.Status != DepositStatus.Paid)
        {
            throw new OperationConflictException(
                request.DepositSessionId,
                $"Deposit cannot refund from status '{EnumWireNames.ToWireName(session.Status)}'.");
        }

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            var rows = await repo.TryTransitionStatusAsync(
                request.TenantId,
                request.DepositSessionId,
                session.Version,
                DepositStatus.Paid,
                DepositStatus.Refunded,
                paidAt: null,
                ct2);

            if (rows == 0)
            {
                throw new OperationConflictException(
                    request.DepositSessionId,
                    "Deposit cannot refund from its current status.");
            }

            await audit.WriteAsync(
                new AuditRecord(
                    TenantId: request.TenantId,
                    ActorType: StaffRole.IsKnown(request.ActorRole) ? request.ActorRole : "operator",
                    ActorId: string.IsNullOrWhiteSpace(request.Actor) ? "unknown" : request.Actor,
                    Action: "deposit.refunded",
                    TargetType: "deposit_session",
                    TargetId: request.DepositSessionId,
                    Decision: "allow",
                    DataClasses: ["deposit"],
                    ReasonCode: string.IsNullOrWhiteSpace(request.ReasonCode) ? null : request.ReasonCode,
                    CorrelationId: request.DepositSessionId,
                    OperationId: session.OperationId),
                ct2);
        }, ct);

        return new RefundDepositResult(request.DepositSessionId, "refunded");
    }
}
