using System.Diagnostics.Metrics;
using Cordia.Domain.Observability;
using Cordia.Infrastructure.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cordia.Tests.UnitTests.Observability;

[Trait("Category", "Unit")]
public class MetricsTests
{
    [Fact]
    public void Metrics_RecordsCounterAndHistogram_FiresMeasurementCallback()
    {
        // Arrange
        using var metrics = new Metrics();

        var counterFired = false;
        long? counterValue = null;
        var histogramFired = false;
        double? histogramValue = null;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == MetricNames.MeterName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == MetricNames.AgentBargeinSuccessTotal)
            {
                counterFired = true;
                counterValue = value;
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            if (instrument.Name == MetricNames.AgentTurnLatencyMs)
            {
                histogramFired = true;
                histogramValue = value;
            }
        });
        listener.Start();

        // Act
        metrics.AgentBargeinSuccessTotal.Add(3);
        metrics.AgentTurnLatencyMs.Record(450.5);

        listener.RecordObservableInstruments();

        // Assert
        Assert.True(counterFired, "Counter measurement callback was not fired.");
        Assert.Equal(3L, counterValue);
        Assert.True(histogramFired, "Histogram measurement callback was not fired.");
        Assert.Equal(450.5, histogramValue);
    }

    [Fact]
    public void AddObservability_EmptyConfig_DoesNotThrowAndResolvesMetrics()
    {
        // Arrange — empty configuration (no OTLP endpoint); proves fail-open behaviour (§8).
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        // Act & Assert — must not throw
        var exception = Record.Exception(() =>
        {
            services.AddObservability(configuration);
            using var provider = services.BuildServiceProvider();
            var resolved = provider.GetRequiredService<Metrics>();
            Assert.NotNull(resolved);
        });

        Assert.Null(exception);
    }
}
