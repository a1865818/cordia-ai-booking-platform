using Cordia.Domain.Observability;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Ambient accessor for the current <see cref="AmbientCorrelationContext"/> (§25.1).
///
/// <para>Unlike <see cref="ITenantContext"/> (request-scoped), correlation is ambient: it must
/// flow across async boundaries and DI scopes (e.g. from the HTTP edge into background work),
/// so the implementation is backed by <c>AsyncLocal</c> and registered as a singleton. This lets
/// the singleton Serilog enricher read the current correlation without DI-scope gymnastics.</para>
/// </summary>
public interface ICorrelationContext
{
    /// <summary>The current ambient context, or <see cref="AmbientCorrelationContext.Empty"/> when unset.</summary>
    AmbientCorrelationContext Current { get; }

    /// <summary>Sets the ambient context for the current async flow.</summary>
    void Set(AmbientCorrelationContext context);
}
