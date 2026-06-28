using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;

namespace Cordia.Infrastructure.Providers;

/// <summary>
/// Spec-06 stub: always reports capacity as available (spec 06 §6.3 seam note).
/// Spec 07 replaces this with the real conditional-write implementation.
/// Registered as singleton — stateless.
/// </summary>
public sealed class AlwaysAvailableCapacityService : ICapacityService
{
    public Task<CapacityResult> TryReserveAsync(
        string tenantId,
        string operationId,
        CancellationToken ct = default)
        => Task.FromResult(CapacityResult.Reserved);
}
