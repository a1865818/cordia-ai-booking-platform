using FluentValidation;

namespace Cordia.Application.Handlers.Notifications.Reconcile;

/// <summary>Validates <see cref="ReconcileNotificationCommand"/> inputs.</summary>
public sealed class ReconcileNotificationValidator : AbstractValidator<ReconcileNotificationCommand>
{
    public ReconcileNotificationValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.NotificationId).NotEmpty();
    }
}
