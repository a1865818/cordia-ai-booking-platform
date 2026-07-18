using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Domain.Exceptions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Cordia.Application.Behaviours;

/// <summary>
/// Spec-14 operator-action spine (§3.1, §24.5). No-op for requests that do not
/// implement <see cref="IOperatorActionRequest"/> — inbox consumers and
/// customer-facing commands are untouched (Inv-5).
///
/// For operator requests it (a) fails closed with a typed
/// <see cref="AuthorizationFailedException"/> (403) when the actor identity,
/// role, or reason code is missing (structural backstop behind the
/// FluentValidation rules) — writing a DENY audit (through a fresh scope, missing
/// fields recorded as "unknown") so the trail is complete, (a2) enforces the
/// <see cref="StaffRole"/> matrix
/// (GD-1: owner-required actions demand owner; operator-required actions accept
/// operator or owner) BEFORE the handler — a mismatch writes a DENY audit
/// through a fresh DI scope and throws the same 403 — and
/// (b) appends a DENY audit event when an <see cref="AuthorizationFailedException"/>
/// escapes the handler — a genuine authorization refusal, not an ordinary
/// business error (those, and self-audited grant denials, flow through
/// untouched). The effect's transaction rolled back, so the denial is recorded
/// in its own transaction. The deny write resolves a FRESH DI
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

        if (string.IsNullOrWhiteSpace(op.ActorId)
            || string.IsNullOrWhiteSpace(op.ActorRole)
            || string.IsNullOrWhiteSpace(op.ReasonCode))
        {
            // Typed 403 (not a 400 validation error): the request is well-formed but the caller
            // has not established WHO is acting, AS WHAT, or WHY — an authorization failure (§24.5).
            // Audit the denial for completeness parity with the role-mismatch and DomainException
            // branches (L6) — even an incomplete identity leaves a tenant + action + reason trail;
            // missing fields are recorded as "unknown". Fresh scope, same rollback-safety rationale.
            using var scope = scopeFactory.CreateScope();
            var denyAudit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
            await denyAudit.WriteAsync(
                new AuditRecord(
                    TenantId: op.TenantId,
                    ActorType: StaffRole.IsKnown(op.ActorRole) ? op.ActorRole : "operator",
                    ActorId: string.IsNullOrWhiteSpace(op.ActorId) ? "unknown" : op.ActorId,
                    Action: op.ActionName,
                    TargetType: op.TargetType,
                    TargetId: op.TargetId,
                    Decision: "deny",
                    DataClasses: ["operations"],
                    ReasonCode: string.IsNullOrWhiteSpace(op.ReasonCode) ? null : op.ReasonCode,
                    Extra: new
                    {
                        error = "identity_incomplete",
                        has_actor = !string.IsNullOrWhiteSpace(op.ActorId),
                        has_role = !string.IsNullOrWhiteSpace(op.ActorRole),
                        has_reason = !string.IsNullOrWhiteSpace(op.ReasonCode),
                    }),
                // CancellationToken.None (B2): the denial record must persist even if the client
                // disconnects and cancels the request token.
                CancellationToken.None);
            throw new AuthorizationFailedException(
                op.ActionName,
                "Operator actions require an actor identity (X-Actor), a staff role (X-Actor-Role), and a reason code (X-Reason-Code) (spec 14 §24.5).");
        }

        if (!StaffRole.Satisfies(op.ActorRole, op.RequiredRole))
        {
            // Role mismatch is a first-class authorization DECISION (§3.1 step 2/5): audit the
            // denial before refusing. Fresh scope for the same reason as the DomainException
            // deny below — never flush through a request context whose work may roll back.
            using var scope = scopeFactory.CreateScope();
            var denyAudit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
            await denyAudit.WriteAsync(
                new AuditRecord(
                    TenantId: op.TenantId,
                    // actor_type must stay within the ratified set; an unrecognized asserted
                    // role is recorded as "operator" with the raw value in Extra.
                    ActorType: StaffRole.IsKnown(op.ActorRole) ? op.ActorRole : "operator",
                    ActorId: op.ActorId,
                    Action: op.ActionName,
                    TargetType: op.TargetType,
                    TargetId: op.TargetId,
                    Decision: "deny",
                    DataClasses: ["operations"],
                    ReasonCode: op.ReasonCode,
                    Extra: new { provided_role = op.ActorRole, required_role = op.RequiredRole, error = "role_not_satisfied" }),
                CancellationToken.None); // B2: durable even if the client disconnects.
            throw new AuthorizationFailedException(
                op.ActionName,
                $"This action requires the '{op.RequiredRole}' role (spec 14 §24.5, GD-1 role matrix).");
        }

        try
        {
            return await next(ct);
        }
        catch (AuthorizationFailedException ex)
        {
            // Narrowed from `catch (DomainException)` (B4): only a genuine AUTHORIZATION denial
            // thrown from inside the handler is a DENY decision. A broad DomainException catch
            // mis-labeled ordinary business failures (e.g. PreorderSagaNotFoundException → 404,
            // InvalidPreorderSagaTransitionException → 409) as denials, and double-counted the
            // ones a self-auditing dependency already recorded (a handler's ConsumeGrantAsync
            // deny-audits before throwing VerificationRequiredException). Those now flow through
            // untouched — only truly-unaudited authz refusals are recorded here.
            using var scope = scopeFactory.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
            await audit.WriteAsync(
                new AuditRecord(
                    TenantId: op.TenantId,
                    // GD-1: audit the ASSERTED role here too — an owner's failed operator
                    // action must reconcile with that owner's allow rows, not read "operator".
                    ActorType: StaffRole.IsKnown(op.ActorRole) ? op.ActorRole : "operator",
                    ActorId: op.ActorId,
                    Action: op.ActionName,
                    TargetType: op.TargetType,
                    TargetId: op.TargetId,
                    Decision: "deny",
                    DataClasses: ["operations"],
                    ReasonCode: op.ReasonCode,
                    Extra: new { error = ex.GetType().Name }),
                CancellationToken.None); // B2: durable even if the client disconnects.
            throw;
        }
    }
}
