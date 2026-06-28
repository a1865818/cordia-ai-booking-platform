namespace Cordia.Domain.Notifications;

/// <summary>Outcome reported by a provider adapter after a send attempt.</summary>
public enum NotificationSendOutcome
{
    Accepted,
    FailedTransient,
    FailedPermanent,
    InvalidRecipient,
}

/// <summary>Result returned by <see cref="Interfaces.INotificationProviderAdapter.SendAsync"/>.</summary>
public sealed record NotificationSendResult(
    NotificationSendOutcome Outcome,
    string? ProviderMessageId,
    string? FailureCategory);
