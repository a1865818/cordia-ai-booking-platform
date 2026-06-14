using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Persistence contract for <see cref="SampleEntity"/>. Implemented in the
/// Infrastructure layer so the Domain stays free of data-access concerns.
/// </summary>
public interface ISampleRepository
{
    Task<IReadOnlyList<SampleEntity>> GetAllAsync(CancellationToken cancellationToken = default);
}
