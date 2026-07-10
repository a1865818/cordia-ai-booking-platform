using Cordia.Application.Common.Audit;
using Cordia.Domain.Entities;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Cordia.Application.Common.Authorization;

/// <summary>
/// Centralized verification-grant scope check for manage-booking reads and
/// mutations. Discovery may identify a booking; this service is what authorizes.
///
/// Spec 14 §3.1 step 5: every DENY decision is audited. The deny audit is
/// written through a FRESH DI scope: callers typically invoke this inside
/// their own unit-of-work transaction, which the thrown
/// <see cref="VerificationRequiredException"/> rolls back — the denial record
/// must survive that rollback.
/// </summary>
public sealed class GrantAuthorizationService(
    IVerificationGrantRepository grants,
    IServiceScopeFactory scopeFactory) : IGrantAuthorizationService
{
    public async Task<VerificationGrant> RequireGrantAsync(
        string tenantId,
        string grantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var grant = await grants.FindValidGrantByIdAsync(
            tenantId,
            grantId,
            customerRef,
            action,
            targetBookingId,
            now,
            ct);

        if (grant is null)
        {
            await WriteDenyAuditAsync(tenantId, grantId, customerRef, action, targetBookingId);
            throw new VerificationRequiredException(tenantId, customerRef, action, targetBookingId);
        }

        return grant;
    }

    public async Task ConsumeGrantAsync(
        string tenantId,
        string grantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId,
        CancellationToken ct = default)
    {
        var rows = await grants.ConsumeAsync(
            tenantId,
            grantId,
            customerRef,
            action,
            targetBookingId,
            DateTimeOffset.UtcNow,
            ct);
        if (rows == 0)
        {
            await WriteDenyAuditAsync(tenantId, grantId, customerRef, action, targetBookingId);
            throw new VerificationRequiredException(tenantId, customerRef, action, targetBookingId);
        }
    }

    private async Task WriteDenyAuditAsync(
        string tenantId,
        string grantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId)
    {
        using var scope = scopeFactory.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
        await audit.WriteAsync(
            new AuditRecord(
                TenantId: tenantId,
                ActorType: "customer",
                ActorId: customerRef,
                Action: "verification.grant_denied",
                TargetType: "booking",
                TargetId: targetBookingId ?? grantId,
                Decision: "deny",
                DataClasses: ["booking", "pii"],
                Extra: new
                {
                    requested_action = action.ToString(),
                    grant_id = grantId,
                }),
            // CancellationToken.None (B2): the denial record must survive both the caller's
            // transaction rollback AND a client disconnect that cancels the request token.
            CancellationToken.None);
    }
}
