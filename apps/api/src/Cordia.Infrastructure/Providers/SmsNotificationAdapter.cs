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
        // A real SMS provider addresses the message with `message.Destination`; this stub has no
        // network I/O, so it records only WHETHER one was resolved. The value itself is never
        // logged — it is the raw phone number, and only the tokenised MinimizedRecipient is safe
        // to emit (Inv-12 hygiene).
        //
        // A missing destination is NOT failed here, because this stub cannot tell "nobody to send
        // to" from "nothing to send over" and refusing would turn a stub limitation into a lane
        // outage. Waitlist offers DO resolve through this resolver now (it grew a waitlist branch
        // that reads the enrollment's customer_ref), so a null destination on that lane means the
        // enrolment carried no deliverable address — a real gap, not a routing quirk. A real
        // adapter must fail closed — see the note in SendNotificationCommandHandler step 7.
        if (string.IsNullOrWhiteSpace(message.Destination))
        {
            logger.LogWarning(
                "SmsNotificationAdapter.SendAsync key={Key} recipient={Recipient} resolved no destination.",
                providerIdempotencyKey, message.MinimizedRecipient);
        }

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
