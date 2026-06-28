using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Notifications;

namespace Cordia.Infrastructure.Providers;

/// <summary>
/// Default consent gate stub that always allows delivery. No real consent store.
/// Per-recipient, per-channel consent storage and quiet-hours enforcement are open
/// decisions (spec 10 §31 / §14 security &amp; privacy). Withdrawn-consent and policy-
/// suppressed paths are exercised by unit tests via mock substitution.
/// </summary>
public sealed class ConsentGate : IConsentGate
{
    /// <inheritdoc/>
    public Task<ConsentDecision> CheckAsync(
        string tenantId,
        string recipientRef,
        NotificationChannel channel,
        CancellationToken ct = default)
        => Task.FromResult(ConsentDecision.Allowed);
}
