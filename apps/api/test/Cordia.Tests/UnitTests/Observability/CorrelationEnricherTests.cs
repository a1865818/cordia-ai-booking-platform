using Cordia.Domain.Observability;
using Cordia.Infrastructure.Observability;
using Cordia.Infrastructure.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Cordia.Tests.UnitTests.Observability;

[Trait("Category", "Unit")]
public class CorrelationEnricherTests
{
    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    [Fact]
    public async Task Enricher_WithCorrelationSet_StampsCorrelationIdProperty()
    {
        // Run in an isolated async flow so the AsyncLocal value is scoped to this test.
        await Task.Run(() =>
        {
            var holder = new CorrelationContextHolder();
            holder.Set(new AmbientCorrelationContext(
                TenantId: "tenant-9",
                CorrelationId: "corr_enriched",
                CausationId: "cause-9"));

            var sink = new CapturingSink();
            using var logger = new LoggerConfiguration()
                .Enrich.With(new SerilogCorrelationEnricher())
                .WriteTo.Sink(sink)
                .CreateLogger();

            logger.Information("hello");

            var evt = Assert.Single(sink.Events);
            Assert.True(evt.Properties.ContainsKey("correlation_id"));
            Assert.Equal("\"corr_enriched\"", evt.Properties["correlation_id"].ToString());
            Assert.True(evt.Properties.ContainsKey("tenant_id"));
            Assert.True(evt.Properties.ContainsKey("causation_id"));
        }, TestContext.Current.CancellationToken);
    }
}
