using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Domain.Exceptions;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Cordia.Application.Behaviours;

/// <summary>
/// Spec-14 operator-action spine (§3.1, §24.5). No-op for requests that do not
/// implement <see cref="IOperatorActionRequest"/> — inbox consumers and
/// customer-facing commands are untouched (Inv-5).
///
/// For operator requests it (a) fails closed when the actor identity or reason
/// code is missing (structural backstop behind the FluentValidation rules) and
/// (b) appends a DENY audit event when a typed <see cref="DomainException"/>
/// escapes the handler — the effect's transaction rolled back, so the denial
/// is recorded in its own transaction. The deny write resolves a FRESH DI
/// scope (fresh DbContext): the request-scoped context's change tracker may
/// still hold entities the rolled-back transaction staged, and flushing the
/// audit through it could durably persist exactly the state the rollback
/// discarded. The ALLOW audit is the handler's job, inside the effect's
/// transaction (see <see cref="IOperatorActionRequest"/>).
///
/// Pipeline order: Validation → OperatorAction → InboxDedup → Handler.
/// </summary>
public sealed class OperatorActionBehavior<TRequest, TResponse>(IServiceScopeFactory scopeFactory)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken ct)
    {
        if (request is not IOperatorActionRequest op)
            return await next(ct);

        if (string.IsNullOrWhiteSpace(op.ActorId) || string.IsNullOrWhiteSpace(op.ReasonCode))
        {
            throw new ValidationException(new[]
            {
                new ValidationFailure(
                    nameof(IOperatorActionRequest.ReasonCode),
                    "Operator actions require an actor identity (X-Actor) and a reason code (spec 14 §24.5)."),
            });
        }

        try
        {
            return await next(ct);
        }
        catch (DomainException ex)
        {
            using var scope = scopeFactory.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
            await audit.WriteAsync(
                new AuditRecord(
                    TenantId: op.TenantId,
                    ActorType: "operator",
                    ActorId: op.ActorId,
                    Action: op.ActionName,
                    TargetType: op.TargetType,
                    TargetId: op.TargetId,
                    Decision: "deny",
                    DataClasses: ["operations"],
                    ReasonCode: op.ReasonCode,
                    Extra: new { error = ex.GetType().Name }),
                ct);
            throw;
        }
    }
}
