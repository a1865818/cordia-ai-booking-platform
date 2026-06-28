using Cordia.Domain.Interfaces;

namespace Cordia.Infrastructure.Providers;

/// <summary>
/// Default <see cref="IValidationPolicy"/> stub that always passes (returns <c>null</c>).
///
/// Spec 04 owns the real policy engine; this singleton seam is replaced in tests to exercise
/// the <c>Validating→Rejected</c> edge (spec 06 §6.1, §11, T11a).
/// Register as singleton (stateless).
/// </summary>
public sealed class AlwaysValidPolicy : IValidationPolicy
{
    public Task<string?> CheckAsync(string tenantId, string operationId, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
