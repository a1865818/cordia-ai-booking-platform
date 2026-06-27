using Cordia.Domain.Interfaces;

namespace Cordia.Infrastructure.Providers;

/// <summary>
/// Default no-op verification service: user action is never required.
/// Spec 08 replaces this with the real grant-based check via IVerificationGrantRepository.
/// </summary>
public sealed class NoUserActionRequiredService : IVerificationService
{
    public Task<bool> RequiresUserActionAsync(
        string tenantId,
        string operationId,
        CancellationToken ct = default)
        => Task.FromResult(false);
}
