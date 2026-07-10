using Cordia.Domain.Entities;
using Cordia.Domain.Generated;

namespace Cordia.Application.Common.Authorization;

/// <summary>Application authorization gate for spec 08 verification grants.</summary>
/// <remarks><c>targetType</c> is REQUIRED (no default): it is the resource the grant authorizes
/// ("booking" for manage-booking flows, "conversation_session" for a transcript read) and drives the
/// deny audit's TargetType + DataClasses. It is deliberately not defaulted so a new non-booking
/// caller cannot silently mis-attribute its denials to a booking (the defect this parameter fixed).</remarks>
public interface IGrantAuthorizationService
{
    Task<VerificationGrant> RequireGrantAsync(
        string tenantId,
        string grantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId,
        string targetType,
        CancellationToken ct = default);

    Task ConsumeGrantAsync(
        string tenantId,
        string grantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId,
        string targetType,
        CancellationToken ct = default);
}
