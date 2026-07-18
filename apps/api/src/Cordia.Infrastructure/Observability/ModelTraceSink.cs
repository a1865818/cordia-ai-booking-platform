using Cordia.Domain.Interfaces;
using Serilog;
using Serilog.Events;

namespace Cordia.Infrastructure.Observability;

/// <summary>
/// Routes model prompts/outputs to a dedicated, stricter-access logging lane (§25.3).
///
/// <para>Every record is stamped with the <c>ModelTrace=true</c> property. The Serilog
/// configuration in <c>Program.cs</c> uses that marker to fork two sinks: the ordinary app sink
/// EXCLUDES events carrying <c>ModelTrace</c>, and a separate model-trace sink INCLUDES ONLY them
/// — so prompts/outputs are never co-mingled with app logs (Inv-12, §25.3).</para>
/// </summary>
public sealed class ModelTraceSink(ILogger logger) : IModelTraceSink
{
    /// <summary>The marker property that fences model traces off from the app log sink.</summary>
    public const string MarkerProperty = "ModelTrace";

    /// <inheritdoc />
    public void Record(string kind, string content, IReadOnlyDictionary<string, string>? attributes = null)
    {
        var enriched = logger.ForContext(MarkerProperty, true);

        if (attributes is not null)
        {
            foreach (var (key, val) in attributes)
                enriched = enriched.ForContext(key, val);
        }

        // Model content is the payload itself, not a redactable structured object — it lands only
        // in the fenced-off model-trace sink, which has stricter access + shorter retention (§25.3).
        enriched.Write(LogEventLevel.Information, "model_trace {Kind}: {Content}", kind, content);
    }
}
