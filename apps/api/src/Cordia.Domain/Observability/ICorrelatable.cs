namespace Cordia.Domain.Observability;

/// <summary>Entities in the §25.1 correlation chain whose correlation_id/causation_id are
/// auto-stamped from the ambient correlation context on insert (spec 13 §4.1, §25.1).</summary>
public interface ICorrelatable
{
    string? CorrelationId { get; set; }
    string? CausationId { get; set; }
}
