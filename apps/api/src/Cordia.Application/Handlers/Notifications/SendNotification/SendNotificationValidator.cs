using FluentValidation;

namespace Cordia.Application.Handlers.Notifications.SendNotification;

/// <summary>Validates the <see cref="SendNotificationCommand"/> before the inbox transaction opens.</summary>
public sealed class SendNotificationValidator : AbstractValidator<SendNotificationCommand>
{
    public SendNotificationValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.OperationId).NotEmpty();
        RuleFor(x => x.MessageId).NotEmpty();
    }
}
