namespace Cordia.Domain.Interfaces;

/// <summary>
/// Whether the operation needs a user action (missing verification/payment) before it can
/// proceed past Validating (spec 08 owns the real grant-based check via IVerificationGrantRepository).
/// </summary>
public interface IVerificationService
{
    Task<bool> RequiresUserActionAsync(
        string tenantId,
        string operationId,
        CancellationToken ct = default);
}
