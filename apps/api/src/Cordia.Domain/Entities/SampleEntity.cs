namespace Cordia.Domain.Entities;

/// <summary>
/// A trivial sample entity demonstrating the EF Core wiring. Replace with real
/// domain entities.
/// </summary>
public class SampleEntity
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
