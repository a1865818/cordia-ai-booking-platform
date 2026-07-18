namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when a request reaches tenant-scoped work without a resolved tenant identity
/// (spec 14 §24.1, Inv-11 fail-closed). Raised by <c>TenantGuard.RequireTenant</c> so
/// controllers share one typed guard instead of hand-rolled checks.
/// Maps to HTTP 400 Bad Request with title "Missing tenant".
/// </summary>
public sealed class TenantScopeMissingException : DomainException
{
    public TenantScopeMissingException()
        : base("X-Tenant-Id header is required.")
    {
    }
}
