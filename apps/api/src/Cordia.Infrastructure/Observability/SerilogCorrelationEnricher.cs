using Cordia.Infrastructure.Services;
using Serilog.Core;
using Serilog.Events;

namespace Cordia.Infrastructure.Observability;

/// <summary>
/// Serilog enricher that stamps the ambient correlation fields onto every log event (§25.1,
/// §25.3). Reads <see cref="CorrelationContextHolder.CurrentOrNull"/> — the static
/// <c>AsyncLocal</c> backing store — so the (singleton) enricher needs no DI scope.
///
/// <para>Only non-null fields are added; an unset context contributes nothing (fail-open).</para>
/// </summary>
public sealed class SerilogCorrelationEnricher : ILogEventEnricher
{
    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var ctx = CorrelationContextHolder.CurrentOrNull;
        if (ctx is null)
            return;

        Add(logEvent, propertyFactory, "tenant_id", ctx.TenantId);
        Add(logEvent, propertyFactory, "correlation_id", ctx.CorrelationId);
        Add(logEvent, propertyFactory, "causation_id", ctx.CausationId);
        Add(logEvent, propertyFactory, "session_id", ctx.SessionId);
        Add(logEvent, propertyFactory, "turn_id", ctx.TurnId);
        Add(logEvent, propertyFactory, "operation_id", ctx.OperationId);
    }

    private static void Add(
        LogEvent logEvent,
        ILogEventPropertyFactory propertyFactory,
        string name,
        string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(name, value));
    }
}
