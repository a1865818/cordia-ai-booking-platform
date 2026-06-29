using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>Durable verification challenge for request/check verification flow.</summary>
public class VerificationChallenge : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string VerificationId { get; set; } = string.Empty;

    public string IdempotencyKey { get; set; } = string.Empty;

    public string? CustomerId { get; set; }

    public string? TargetBookingId { get; set; }

    public VerificationActionEnum Action { get; set; }

    public VerificationRequestChannel Channel { get; set; }

    public string CodeHash { get; set; } = string.Empty;

    public DateTimeOffset RequestedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? VerifiedAt { get; set; }

    public string? GrantId { get; set; }
}
