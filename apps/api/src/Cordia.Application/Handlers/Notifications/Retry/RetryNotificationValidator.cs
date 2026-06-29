using FluentValidation;

namespace Cordia.Application.Handlers.Notifications.Retry;

/// <summary>FluentValidation validator for <see cref="RetryNotificationCommand"/>.</summary>
public class RetryNotificationValidator : AbstractValidator<RetryNotificationCommand>
{
    public RetryNotificationValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.NotificationId).NotEmpty();
        RuleFor(x => x.MessageId).NotEmpty();
    }
}
