using Cordia.Domain.Generated;

namespace Cordia.Domain.Exceptions;

/// <summary>
/// Raised when a sensitive read or manage-booking mutation lacks a valid grant
/// for the requested tenant/customer/booking/action scope.
/// </summary>
public sealed class VerificationRequiredException : DomainException
{
    public VerificationRequiredException(
        string tenantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId)
        : base(BuildMessage(tenantId, customerRef, action, targetBookingId))
    {
        TenantId = tenantId;
        CustomerRef = customerRef;
        Action = action;
        TargetBookingId = targetBookingId;
    }

    public string TenantId { get; }

    public string CustomerRef { get; }

    public VerificationActionEnum Action { get; }

    public string? TargetBookingId { get; }

    private static string BuildMessage(
        string tenantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId)
    {
        var target = string.IsNullOrWhiteSpace(targetBookingId) ? "customer scope" : $"booking '{targetBookingId}'";
        return $"Verification is required for tenant '{tenantId}', customer '{customerRef}', action '{action}', {target}.";
    }
}
