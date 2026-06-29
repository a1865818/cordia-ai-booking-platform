using FluentValidation;

namespace Cordia.Application.Handlers.Notifications.MissingTerminalAudit;

/// <summary>Validates <see cref="AuditMissingTerminalNotificationCommand"/>.</summary>
public sealed class AuditMissingTerminalNotificationValidator
    : AbstractValidator<AuditMissingTerminalNotificationCommand>
{
    public AuditMissingTerminalNotificationValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.OperationId).NotEmpty();
    }
}
