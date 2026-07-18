using System.Diagnostics.Metrics;
using Cordia.Infrastructure.Observability;

namespace Cordia.Tests.UnitTests.Observability;

[Trait("Category", "Unit")]
public class GaugeTests : IDisposable
{
    private readonly Metrics _metrics = new();
    private readonly MeterListener _listener = new();

    public GaugeTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter == _metrics.Meter)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _metrics.Dispose();
    }

    [Fact]
    public void DlqDepth_SetValue_ObservableGaugeReportsCorrectValue()
    {
        _metrics.DlqDepth = 7;

        long reported = 0;
        _listener.SetMeasurementEventCallback<long>((inst, measurement, _, _) =>
        {
            if (inst.Name == "fulfillment.dlq.depth")
                reported = measurement;
        });
        _listener.RecordObservableInstruments();

        Assert.Equal(7, reported);
    }

    [Fact]
    public void DlqOldestAgeSeconds_SetValue_ObservableGaugeReportsCorrectValue()
    {
        _metrics.DlqOldestAgeSeconds = 42.5;

        double reported = 0;
        _listener.SetMeasurementEventCallback<double>((inst, measurement, _, _) =>
        {
            if (inst.Name == "fulfillment.dlq.oldest_age_seconds")
                reported = measurement;
        });
        _listener.RecordObservableInstruments();

        Assert.Equal(42.5, reported, precision: 1);
    }

    [Fact]
    public void ManualReviewDepth_SetValue_ObservableGaugeReportsCorrectValue()
    {
        _metrics.ManualReviewDepth = 3;

        long reported = 0;
        _listener.SetMeasurementEventCallback<long>((inst, measurement, _, _) =>
        {
            if (inst.Name == "fulfillment.manual_review.depth")
                reported = measurement;
        });
        _listener.RecordObservableInstruments();

        Assert.Equal(3, reported);
    }

    [Fact]
    public void ManualReviewOldestAgeSeconds_SetValue_ObservableGaugeReportsCorrectValue()
    {
        _metrics.ManualReviewOldestAgeSeconds = 99.0;

        double reported = 0;
        _listener.SetMeasurementEventCallback<double>((inst, measurement, _, _) =>
        {
            if (inst.Name == "fulfillment.manual_review.oldest_age_seconds")
                reported = measurement;
        });
        _listener.RecordObservableInstruments();

        Assert.Equal(99.0, reported, precision: 1);
    }
}
