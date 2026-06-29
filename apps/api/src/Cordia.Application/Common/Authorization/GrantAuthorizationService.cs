using Cordia.Domain.Entities;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;

namespace Cordia.Application.Common.Authorization;

/// <summary>
/// Centralized verification-grant scope check for manage-booking reads and
/// mutations. Discovery may identify a booking; this service is what authorizes.
/// </summary>
public sealed class GrantAuthorizationService(IVerificationGrantRepository grants) : IGrantAuthorizationService
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

        return grant ?? throw new VerificationRequiredException(tenantId, customerRef, action, targetBookingId);
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
            throw new VerificationRequiredException(tenantId, customerRef, action, targetBookingId);
    }
}
