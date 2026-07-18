using System.IO;
using Cordia.Infrastructure.Observability;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace Cordia.Tests.UnitTests.Observability;

[Trait("Category", "Unit")]
public class RedactionTests
{
    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    [Fact]
    public void Destructuring_SensitiveAndPanValues_AreRedacted()
    {
        // Arrange
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration()
            .Destructure.With(new RedactingDestructuringPolicy())
            .WriteTo.Sink(sink)
            .CreateLogger();

        var payload = new
        {
            Pan = "4111111111111111",
            Cvv = "123",
            Secret = "hunter2",
            Transcript = "caller said the magic words",
            CustomerName = "Ada Lovelace",
        };

        // Act
        logger.Information("payment {@Payload}", payload);

        // Render the captured event to text the way a real sink would, so we assert on output.
        Assert.Single(sink.Events);
        var rendered = Render(sink.Events[0]);

        // Assert — none of the sensitive values reach the sink, marker present, allowed field survives.
        Assert.DoesNotContain("4111111111111111", rendered);
        Assert.DoesNotContain("hunter2", rendered);
        Assert.DoesNotContain("caller said", rendered);
        // The CVV "123" is redacted by name; ensure the redaction marker is present.
        Assert.Contains(RedactingDestructuringPolicy.RedactedMarker, rendered);
        // Non-sensitive field is logged verbatim.
        Assert.Contains("Ada Lovelace", rendered);
    }

    [Fact]
    public void Destructuring_BarePanString_InNonSensitiveField_IsRedacted()
    {
        // Arrange — a PAN-looking value in a field whose name is NOT on the denylist.
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration()
            .Destructure.With(new RedactingDestructuringPolicy())
            .WriteTo.Sink(sink)
            .CreateLogger();

        var payload = new { Note = "the number is 4111-1111-1111-1111 ok" };

        // Act
        logger.Information("note {@Payload}", payload);

        // Assert
        var rendered = Render(sink.Events[0]);
        Assert.DoesNotContain("4111", rendered);
        Assert.Contains(RedactingDestructuringPolicy.RedactedMarker, rendered);
    }

    [Theory]
    [InlineData("1234567890123")]            // 13-digit PAN (minimum)
    [InlineData("1234567890123456")]         // 16-digit PAN
    [InlineData("1234567890123456789")]      // 19-digit PAN (maximum)
    [InlineData("4111 1111 1111 1111")]      // space-separated
    [InlineData("4111-1111-1111-1111")]      // dash-separated
    public void Destructuring_PanLikeValues_AreRedacted(string pan)
    {
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration()
            .Destructure.With(new RedactingDestructuringPolicy())
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("v {@Payload}", new { Note = $"value {pan} here" });

        var rendered = Render(sink.Events[0]);
        Assert.DoesNotContain(pan, rendered);
        Assert.Contains(RedactingDestructuringPolicy.RedactedMarker, rendered);
    }

    [Theory]
    [InlineData("123456789012")]    // 12 digits — too short to be a PAN
    [InlineData("12345678901234567890")] // 20 digits — too long
    public void Destructuring_NonPanDigitRuns_InNonSensitiveField_AreNotRedacted(string digits)
    {
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration()
            .Destructure.With(new RedactingDestructuringPolicy())
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("v {@Payload}", new { Reference = digits });

        var rendered = Render(sink.Events[0]);
        Assert.Contains(digits, rendered);
    }

    [Fact]
    public void LoggingWithoutDestructuring_BypassesPolicy_DocumentsConvention()
    {
        // A destructuring policy ONLY fires for values logged with the `@` operator. A sensitive
        // scalar interpolated via its ToString() ({Card}) bypasses redaction — this is an inherent
        // Serilog limitation, so the codebase convention is: ALWAYS log payloads with `@`.
        // This test pins that behaviour so the convention is explicit and any future change is noticed.
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration()
            .Destructure.With(new RedactingDestructuringPolicy())
            .WriteTo.Sink(sink)
            .CreateLogger();

        const string pan = "4111111111111111";
        logger.Information("card {Card}", pan); // NOTE: no `@` — intentionally bypasses the policy.

        var rendered = Render(sink.Events[0]);
        Assert.Contains(pan, rendered); // documents the bypass; do NOT log sensitive scalars this way.
    }

    private static string Render(LogEvent logEvent)
    {
        var formatter = new JsonFormatter();
        using var sw = new StringWriter();
        formatter.Format(logEvent, sw);
        return sw.ToString();
    }
}
