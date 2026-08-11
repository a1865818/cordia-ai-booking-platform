using Cordia.Application.Common.Audit;
using Cordia.Domain.Entities;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    IServiceScopeFactory scopeFactory,
    ILogger<GrantAuthorizationService> logger) : IGrantAuthorizationService
{
    public async Task<VerificationGrant> RequireGrantAsync(
        string tenantId,
        string grantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId,
        string targetType,
        string? actorId = null,
        string? actorRole = null,
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
            await WriteDenyAuditAsync(
                tenantId, grantId, customerRef, action, targetBookingId, targetType,
                reason: null, actorId, actorRole);
            throw new VerificationRequiredException(tenantId, customerRef, action, targetBookingId);
        }

        return grant;
    }

    public async Task<VerificationGrant> RequireGrantScopeAsync(
        string tenantId,
        string grantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId,
        string targetType,
        string? actorId = null,
        string? actorRole = null,
        CancellationToken ct = default)
    {
        var grant = await grants.FindGrantByIdIgnoringConsumptionAsync(
            tenantId,
            grantId,
            customerRef,
            action,
            targetBookingId,
            DateTimeOffset.UtcNow,
            ct);

        if (grant is null)
        {
            await WriteDenyAuditAsync(
                tenantId, grantId, customerRef, action, targetBookingId, targetType,
                reason: null, actorId, actorRole);
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
        string targetType,
        string? actorId = null,
        string? actorRole = null,
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
            await WriteDenyAuditAsync(
                tenantId, grantId, customerRef, action, targetBookingId, targetType,
                reason: null, actorId, actorRole);
            throw new VerificationRequiredException(tenantId, customerRef, action, targetBookingId);
        }
    }

    public Task WriteDenyAsync(
        string tenantId,
        string? grantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId,
        string targetType,
        string reason,
        string? actorId = null,
        string? actorRole = null)
        => WriteDenyAuditAsync(
            tenantId, grantId ?? string.Empty, customerRef, action, targetBookingId, targetType,
            reason, actorId, actorRole);

    private async Task WriteDenyAuditAsync(
        string tenantId,
        string grantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId,
        string targetType,
        string? reason = null,
        string? actorId = null,
        string? actorRole = null)
    {
        // The deny audit is best-effort relative to the DENIAL ITSELF: both callers write it
        // immediately BEFORE throwing VerificationRequiredException (the 403). If the audit store
        // is unavailable, an unhandled exception here would propagate out of RequireGrantAsync/
        // ConsumeGrantAsync and surface as a generic 500 — converting an authorization DENIAL into
        // an apparent server error and masking the security decision from the caller. Swallow the
        // audit-write failure (logged Warning for the trail) so the intended 403 is always the
        // thrown outcome. The audit-record gap is the lesser evil versus a mislabeled 500.
        try
        {
            using var scope = scopeFactory.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
            await audit.WriteAsync(
                new AuditRecord(
                    TenantId: tenantId,
                    // A staff-asserted caller is recorded as staff — but ONLY when they actually
                    // named themselves. A role with no actor id is not an identification, and
                    // pairing it with a blank ActorId produced the one thing a deny record must
                    // never be: an "operator was refused" row that cannot say which operator.
                    // Falling back to the customer ref (blank on this door's pre-grant denials)
                    // made it worse by attributing a staff attempt to nobody at all.
                    ActorType: StaffRole.IsKnown(actorRole) && !string.IsNullOrWhiteSpace(actorId)
                        ? actorRole!
                        : "customer",
                    ActorId: FirstNonBlank(actorId, customerRef) ?? "unknown",
                    Action: "verification.grant_denied",
                    // The resource type the grant authorizes — "booking" by default, but a transcript
                    // read (targetBookingId null, from GetTranscript) is a "conversation_session", not a
                    // booking. Hard-coding "booking" misattributed transcript denials to a booking
                    // resource and broke audit-trail integrity for sensitive-read refusals.
                    TargetType: targetType,
                    TargetId: targetBookingId ?? grantId,
                    Decision: "deny",
                    // Keep DataClasses in lockstep with TargetType (was hard-coded ["booking","pii"],
                    // which mislabeled a transcript denial's data classes just as the target type was
                    // mislabeled). A conversation_session read exposes [pii, transcript], not booking data.
                    DataClasses: targetType == "conversation_session"
                        ? ["pii", "transcript"]
                        : ["booking", "pii"],
                    Extra: new
                    {
                        requested_action = action.ToString(),
                        grant_id = grantId,
                        reason,
                    }),
                // CancellationToken.None (B2): the denial record must survive both the caller's
                // transaction rollback AND a client disconnect that cancels the request token.
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to write verification deny audit for tenant {TenantId}, action {Action}, target {TargetType}; proceeding with the 403 denial",
                tenantId,
                action,
                targetType);
        }
    }

    private static string? FirstNonBlank(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;
        }

        return null;
    }
}
