using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Domain.TenantResolution;
using Cordia.Infrastructure.Services;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Cordia.Application.Handlers.Auth.Login;

/// <summary>
/// Authenticates a console operator by email + password, mints an <c>opctx+jws</c> session,
/// and audits <c>auth.login</c>. Unknown email, disabled/ambiguous email, and wrong password
/// all return null (identical 401 at the controller — no session row).
/// </summary>
public sealed class LoginCommandHandler(
    IOperatorRepository operators,
    IOperatorSessionRepository sessions,
    IOperatorSessionSigner signer,
    IPasswordHasher<Operator> passwords,
    IAuditWriter audit,
    ICorrelationContext correlation,
    IOptions<OperatorSessionOptions> options,
    IUnitOfWork uow,
    TimeProvider timeProvider)
    : IRequestHandler<LoginCommand, LoginResult?>
{
    private const string EventType = "auth.login";

    public async Task<LoginResult?> Handle(LoginCommand command, CancellationToken ct)
    {
        var email = command.Email.Trim();
        var op = await operators.FindByEmailAcrossTenantsAsync(email, ct);
        if (op is null)
            return null;

        var verify = passwords.VerifyHashedPassword(op, op.PasswordHash, command.Password);
        if (verify is PasswordVerificationResult.Failed)
            return null;

        var now = timeProvider.GetUtcNow();
        var sessionId = $"opsess_{Guid.NewGuid():N}";
        var expiresAt = now + TimeSpan.FromSeconds(options.Value.TtlSeconds);
        var correlationId = ResolveCorrelationId();

        var claims = new OperatorSessionClaims(
            TenantId: op.TenantId,
            ActorId: op.OperatorId,
            ActorRole: op.Role,
            SessionId: sessionId,
            IssuedAt: now,
            ExpiresAt: expiresAt);

        var jws = signer.Sign(claims);

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            await sessions.AddAsync(
                new OperatorSession
                {
                    TenantId = op.TenantId,
                    SessionId = sessionId,
                    OperatorId = op.OperatorId,
                    IssuedAt = now,
                    ExpiresAt = expiresAt,
                    CorrelationId = correlationId,
                },
                ct2);

            await audit.WriteAsync(
                new AuditRecord(
                    TenantId: op.TenantId,
                    ActorType: StaffRole.IsKnown(op.Role) ? op.Role : "operator",
                    ActorId: op.OperatorId,
                    Action: EventType,
                    TargetType: "operator_session",
                    TargetId: sessionId,
                    Decision: "allow",
                    DataClasses: [],
                    CorrelationId: correlationId,
                    OccurredAt: now,
                    Extra: new
                    {
                        tenant_id = op.TenantId,
                        outcome = "authenticated",
                    }),
                ct2);
        }, ct);

        return new LoginResult(jws, op.OperatorId, op.Role, op.TenantId, expiresAt);
    }

    private string ResolveCorrelationId()
    {
        var ambient = correlation.Current.CorrelationId;
        return string.IsNullOrEmpty(ambient) ? $"corr_{Guid.NewGuid():N}" : ambient;
    }
}
