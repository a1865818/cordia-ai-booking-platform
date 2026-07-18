using Cordia.Domain.Waitlist;
using MediatR;

namespace Cordia.Application.Handlers.Waitlist.Enroll;

/// <summary>
/// Enrolls a customer on the waitlist (spec 11 §4.4). Returns the new enrollment id. The optional
/// <see cref="Deposit"/> / <see cref="AutoBook"/> value objects are persisted as jsonb on the
/// enrollment. Auto-book is honoured only within the recorded grant's constraints (§20).
/// </summary>
public sealed record EnrollWaitlistCommand(
    string TenantId,
    string Actor,
    string CustomerRef,
    DateTimeOffset AcceptableFrom,
    DateTimeOffset AcceptableTo,
    int PartySize,
    string LocationId,
    string ServiceId,
    int OfferLifetimeSeconds,
    DepositTerms? Deposit,
    string NotificationChannel,
    AutoBookGrant? AutoBook,
    // Spec 11 §5.1 (divergence #1) — identity context stamped onto the acceptance confirmation.
    // Optional: default to CustomerRef / "unverified" so the accept never over-claims assurance.
    string? IdentityRef = null,
    string? VerificationLevel = null) : IRequest<string>;
