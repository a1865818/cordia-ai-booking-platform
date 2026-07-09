using Cordia.Api.Logging;
using Serilog.Events;
using Serilog.Parsing;

namespace Cordia.Tests.UnitTests.Security;

[Trait("Category", "Unit")]
public class LogScrubbingEnricherTests
{
    private static LogEvent MakeEvent(params (string Name, object Value)[] properties)
        => new(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("test {Placeholder}"),
            properties.Select(p => new LogEventProperty(p.Name, new ScalarValue(p.Value))));

    private static string? GetString(LogEvent e, string name)
        => (e.Properties[name] as ScalarValue)?.Value as string;

    [Theory]
    [InlineData("Password")]
    [InlineData("client_secret")]
    [InlineData("ApiKey")]
    [InlineData("Authorization")]
    [InlineData("ProviderCredential")]
    public void CredentialNamedProperties_AreMaskedWholesale(string name)
    {
        var logEvent = MakeEvent((name, "super-sensitive-value"));

        new LogScrubbingEnricher().Enrich(logEvent, null!);

        Assert.Equal(LogScrubbingEnricher.MaskToken, GetString(logEvent, name));
    }

    [Fact]
    public void PanShapedValues_AreMaskedInAnyProperty()
    {
        var logEvent = MakeEvent(("Note", "customer said 4111 1111 1111 1111 aloud"));

        new LogScrubbingEnricher().Enrich(logEvent, null!);

        var value = GetString(logEvent, "Note");
        Assert.DoesNotContain("4111", value);
        Assert.Contains(LogScrubbingEnricher.MaskToken, value);
    }

    [Fact]
    public void NestedDestructuredValues_AreScrubbedToo()
    {
        // {@Object} destructuring produces StructureValue/SequenceValue trees —
        // a PAN or credential inside them must not bypass the mask (Inv-12).
        var structure = new StructureValue(
        [
            new LogEventProperty("CardNumber", new ScalarValue("4111111111111111")),
            new LogEventProperty("ApiKey", new ScalarValue("sk_live_abc")),
            new LogEventProperty(
                "Notes",
                new SequenceValue([new ScalarValue("pan 4111 1111 1111 1111 spoken")])),
        ]);
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("test"),
            [new LogEventProperty("Request", structure)]);

        new LogScrubbingEnricher().Enrich(logEvent, null!);

        var scrubbed = (StructureValue)logEvent.Properties["Request"];
        var byName = scrubbed.Properties.ToDictionary(p => p.Name, p => p.Value);
        Assert.Equal(LogScrubbingEnricher.MaskToken, ((ScalarValue)byName["CardNumber"]).Value);
        Assert.Equal(LogScrubbingEnricher.MaskToken, ((ScalarValue)byName["ApiKey"]).Value);
        var note = (string)((ScalarValue)((SequenceValue)byName["Notes"]).Elements[0]).Value!;
        Assert.DoesNotContain("4111", note);
        Assert.Contains(LogScrubbingEnricher.MaskToken, note);
    }

    [Fact]
    public void BenignProperties_AreLeftUntouched()
    {
        var logEvent = MakeEvent(
            ("TenantId", "tenant-a"),
            ("PartySize", 4),
            ("Reference", "4111111111111112")); // non-Luhn — must survive

        new LogScrubbingEnricher().Enrich(logEvent, null!);

        Assert.Equal("tenant-a", GetString(logEvent, "TenantId"));
        Assert.Equal("4111111111111112", GetString(logEvent, "Reference"));
        Assert.Equal(4, (logEvent.Properties["PartySize"] as ScalarValue)?.Value);
    }
}
