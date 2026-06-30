using Cordia.Infrastructure.Observability;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Cordia.Tests.UnitTests.Observability;

[Trait("Category", "Unit")]
public class ModelTraceSinkTests
{
    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    [Fact]
    public void Record_LandsInModelTraceSink_NotInAppSink()
    {
        // Arrange — mirror Program.cs two-sink routing on the ModelTrace marker.
        var appSink = new CapturingSink();
        var modelSink = new CapturingSink();

        using var logger = new LoggerConfiguration()
            .WriteTo.Logger(l => l
                .Filter.ByExcluding(e => e.Properties.ContainsKey(ModelTraceSink.MarkerProperty))
                .WriteTo.Sink(appSink))
            .WriteTo.Logger(l => l
                .Filter.ByIncludingOnly(e => e.Properties.ContainsKey(ModelTraceSink.MarkerProperty))
                .WriteTo.Sink(modelSink))
            .CreateLogger();

        var sink = new ModelTraceSink(logger);

        // Act
        logger.Information("ordinary app log line");
        sink.Record("prompt", "what is the capital of France?");

        // Assert — model trace fenced off from app sink.
        Assert.Single(modelSink.Events);
        Assert.Contains(modelSink.Events, e => e.Properties.ContainsKey(ModelTraceSink.MarkerProperty));

        Assert.Single(appSink.Events);
        Assert.DoesNotContain(appSink.Events, e => e.Properties.ContainsKey(ModelTraceSink.MarkerProperty));
        Assert.DoesNotContain(appSink.Events, e => e.RenderMessage().Contains("capital of France"));
    }
}
