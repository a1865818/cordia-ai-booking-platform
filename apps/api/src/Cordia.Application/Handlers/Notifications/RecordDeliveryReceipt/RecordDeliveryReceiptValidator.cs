using FluentValidation;

namespace Cordia.Application.Handlers.Notifications.RecordDeliveryReceipt;

/// <summary>Validates required fields on <see cref="RecordDeliveryReceiptCommand"/>.</summary>
public sealed class RecordDeliveryReceiptValidator : AbstractValidator<RecordDeliveryReceiptCommand>
{
    public RecordDeliveryReceiptValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.NotificationId).NotEmpty();
        RuleFor(x => x.ProviderMessageId).NotEmpty();
    }
}
