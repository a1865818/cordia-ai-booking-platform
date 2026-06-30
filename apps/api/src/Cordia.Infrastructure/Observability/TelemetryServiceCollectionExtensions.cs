using Cordia.Domain.Interfaces;
using Cordia.Domain.Observability;
using Cordia.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;

namespace Cordia.Infrastructure.Observability;

/// <summary>
/// Wires up OpenTelemetry tracing and metrics for the Cordia API (spec 13 §3, §8).
/// </summary>
public static class TelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="Metrics"/> singleton and configures the OpenTelemetry
    /// tracer/meter provider. OTLP export is only added when an endpoint is configured
    /// (fail-open: §8 "Telemetry backend unavailable → fail-open").
    /// </summary>
    public static IServiceCollection AddObservability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<Metrics>();

        // §25.1 correlation plane: ambient (AsyncLocal-backed) so it flows across async + DI scopes
        // and is readable by the singleton Serilog enricher. Singleton by design.
        services.AddSingleton<ICorrelationContext, CorrelationContextHolder>();

        // §25.3 model-trace sink: routes prompts/outputs to the fenced-off Serilog lane
        // (ModelTrace=true). Resolves the configured root Serilog logger.
        services.AddSingleton<IModelTraceSink>(_ => new ModelTraceSink(Serilog.Log.Logger));

        var builder = services
            .AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName: "cordia-api"))
            .WithTracing(t =>
            {
                t.AddAspNetCoreInstrumentation();
                t.AddHttpClientInstrumentation();
                t.AddSource(MetricNames.ActivitySourceName);
            })
            .WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation();
                m.AddHttpClientInstrumentation();
                m.AddMeter(MetricNames.MeterName);
            });

        // §8 Fail-open: only attach OTLP exporter when an endpoint is configured.
        // Business path must never block on telemetry infrastructure unavailability.
        var otlpEndpoint =
            Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
            ?? configuration["Observability:Otlp:Endpoint"];

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            builder
                .WithTracing(t => t.AddOtlpExporter())
                .WithMetrics(m => m.AddOtlpExporter());
        }

        return services;
    }
}
