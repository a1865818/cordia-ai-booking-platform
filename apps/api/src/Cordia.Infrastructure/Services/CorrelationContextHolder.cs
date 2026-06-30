using Cordia.Domain.Interfaces;
using Cordia.Domain.Observability;

namespace Cordia.Infrastructure.Services;

/// <summary>
/// <see cref="AsyncLocal{T}"/>-backed ambient holder for the current
/// <see cref="AmbientCorrelationContext"/> (§25.1).
///
/// <para>Registered as a singleton because the correlation must flow across async boundaries and
/// DI scopes; the <c>AsyncLocal</c> backing store does that automatically and is observed by both
/// the request scope and the singleton Serilog enricher (via <see cref="CurrentOrNull"/>).</para>
/// </summary>
public sealed class CorrelationContextHolder : ICorrelationContext
{
    private static readonly AsyncLocal<AmbientCorrelationContext?> _current = new();

    /// <inheritdoc />
    public AmbientCorrelationContext Current => _current.Value ?? AmbientCorrelationContext.Empty;

    /// <inheritdoc />
    public void Set(AmbientCorrelationContext context) => _current.Value = context;

    /// <summary>
    /// The raw ambient value (or <see langword="null"/> when unset), exposed statically so the
    /// singleton <c>SerilogCorrelationEnricher</c> can read it without a DI dependency.
    /// </summary>
    public static AmbientCorrelationContext? CurrentOrNull => _current.Value;
}
