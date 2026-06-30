using Cordia.Domain.Entities;
using Cordia.Domain.Generated;

namespace Cordia.Application.Common.Authorization;

/// <summary>Application authorization gate for spec 08 verification grants.</summary>
public interface IGrantAuthorizationService
{
    Task<VerificationGrant> RequireGrantAsync(
        string tenantId,
        string grantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId,
        CancellationToken ct = default);

    Task ConsumeGrantAsync(
        string tenantId,
        string grantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId,
        CancellationToken ct = default);
}
