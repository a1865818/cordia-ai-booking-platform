using System.Security.Cryptography;
using System.Text;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Notifications;
using Microsoft.Extensions.Logging;

namespace Cordia.Infrastructure.Providers;

/// <summary>
/// Deterministic SMS notification adapter stub used for development. No real network I/O.
/// Real provider selection is an open decision (spec 10 §31); this stub returns an
/// <see cref="NotificationSendOutcome.Accepted"/> result with a deterministic provider
/// message id derived from the idempotency key, so Send and LookupByKey are idempotent.
/// </summary>
public sealed class SmsNotificationAdapter(ILogger<SmsNotificationAdapter> logger) : INotificationProviderAdapter
{
    /// <inheritdoc/>
    public NotificationChannel Channel => NotificationChannel.Sms;

    /// <inheritdoc/>
    public bool SupportsSendIdempotency => true;

    /// <inheritdoc/>
    public bool SupportsLookup => true;

    /// <inheritdoc/>
    public Task<NotificationSendResult> SendAsync(
        string providerIdempotencyKey,
        RenderedMessage message,
        CancellationToken ct = default)
    {
        var providerMessageId = DeterministicProviderMessageId(providerIdempotencyKey);
        logger.LogInformation(
            "SmsNotificationAdapter.SendAsync key={Key} pmsg={Pmsg}", providerIdempotencyKey, providerMessageId);
        return Task.FromResult(new NotificationSendResult(
            NotificationSendOutcome.Accepted,
            ProviderMessageId: providerMessageId,
            FailureCategory: null));
    }

    /// <inheritdoc/>
    public Task<NotificationSendResult?> LookupByKeyAsync(
        string providerIdempotencyKey,
        CancellationToken ct = default)
    {
        var providerMessageId = DeterministicProviderMessageId(providerIdempotencyKey);
        return Task.FromResult<NotificationSendResult?>(new NotificationSendResult(
            NotificationSendOutcome.Accepted,
            ProviderMessageId: providerMessageId,
            FailureCategory: null));
    }

    private static string DeterministicProviderMessageId(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return "pmsg_" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
