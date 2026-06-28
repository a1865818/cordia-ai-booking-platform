using System.Security.Cryptography;
using System.Text;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Notifications;

namespace Cordia.Infrastructure.Providers;

/// <summary>
/// Deterministic recipient resolver stub used for development. Returns a minimized,
/// non-PII recipient handle derived from the tenant and operation ids.
///
/// <para>Real recipient store lookup, PII minimization, and channel preferences are
/// open decisions (spec 10 §31 / §14). This stub carries NO personal data — the
/// <c>RecipientRef</c> is a deterministic hash-derived opaque handle.</para>
/// </summary>
public sealed class NotificationRecipientResolver : INotificationRecipientResolver
{
    /// <inheritdoc/>
    public Task<ResolvedRecipient?> ResolveAsync(
        string tenantId,
        string operationId,
        CancellationToken ct = default)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}|{operationId}"));
        var recipientRef = "recip_" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
        return Task.FromResult<ResolvedRecipient?>(
            new ResolvedRecipient(RecipientRef: recipientRef, Channel: NotificationChannel.Sms, Locale: "en"));
    }
}
