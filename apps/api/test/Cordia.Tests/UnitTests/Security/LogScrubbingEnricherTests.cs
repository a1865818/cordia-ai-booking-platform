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
    public void NumericPanScalars_AreMasked_BenignNumbersUntouched()
    {
        // Parity with SanitizeJson's numeric branch (Inv-12): {Card} logged as the NUMBER
        // 4111111111111111L must mask like the string form; a double formats fixed-point so
        // scientific notation can't smuggle the digits past detection. A 13-digit non-Luhn
        // epoch-millis long and an ordinary int pass through byte-identical.
        var logEvent = MakeEvent(
            ("Card", 4111111111111111L),
            ("CardAsDouble", 4111111111111111.0d),
            ("OccurredAtMs", 1719878400000L),
            ("PartySize", 4));

        new LogScrubbingEnricher().Enrich(logEvent, null!);

        Assert.Equal(LogScrubbingEnricher.MaskToken, GetString(logEvent, "Card"));
        Assert.Equal(LogScrubbingEnricher.MaskToken, GetString(logEvent, "CardAsDouble"));
        Assert.Equal(1719878400000L, (logEvent.Properties["OccurredAtMs"] as ScalarValue)?.Value);
        Assert.Equal(4, (logEvent.Properties["PartySize"] as ScalarValue)?.Value);
    }

    [Fact]
    public void NestedScalarsUnderCredentialNamedProperty_AreMasked()
    {
        // Bug 2: logger.Information("{@Password}", new { Value = "hunter2" }) left "hunter2"
        // in the log — the recursion re-derived sensitivity from the CHILD's name ("Value"),
        // which is innocuous. A credential-like name must poison the whole subtree.
        var structure = new StructureValue(
        [
            new LogEventProperty("Value", new ScalarValue("hunter2")),
            new LogEventProperty("Pin", new ScalarValue(123456L)), // numeric leaves must mask too
            new LogEventProperty(
                "Aliases",
                new SequenceValue([new ScalarValue("also-sensitive")])),
        ]);
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("test"),
            [new LogEventProperty("Password", structure)]);

        new LogScrubbingEnricher().Enrich(logEvent, null!);

        var scrubbed = (StructureValue)logEvent.Properties["Password"];
        var byName = scrubbed.Properties.ToDictionary(p => p.Name, p => p.Value);
        Assert.Equal(LogScrubbingEnricher.MaskToken, ((ScalarValue)byName["Value"]).Value);
        Assert.Equal(LogScrubbingEnricher.MaskToken, ((ScalarValue)byName["Pin"]).Value);
        Assert.Equal(
            LogScrubbingEnricher.MaskToken,
            ((ScalarValue)((SequenceValue)byName["Aliases"]).Elements[0]).Value);
    }

    [Fact]
    public void DictionaryKeys_WithPanText_AreMaskedAndDeduplicated()
    {
        // Dictionary KEYS render into log output too — a PAN used as a key must mask
        // like a value (Inv-12), whether the key is a string OR a numeric scalar. Three
        // DISTINCT PAN keys all mask toward the same token; without dedupe,
        // DictionaryValue's dictionary-backed ctor throws, Serilog swallows the
        // enricher exception, and the event emits UNMASKED (fail open).
        var dict = new DictionaryValue(
        [
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                new ScalarValue("4111111111111111"), new ScalarValue("visa")),
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                new ScalarValue("378282246310005"), new ScalarValue("amex")),
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                new ScalarValue(4111111111111112L), new ScalarValue("visa-long")),
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                new ScalarValue("channel"), new ScalarValue("voice")),
        ]);
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("test"),
            [new LogEventProperty("Cards", dict)]);

        new LogScrubbingEnricher().Enrich(logEvent, null!);

        var scrubbed = (DictionaryValue)logEvent.Properties["Cards"];
        var keys = scrubbed.Elements.Keys.Select(k => k.Value?.ToString()).ToArray();
        Assert.Equal(4, keys.Length);
        Assert.DoesNotContain("4111111111111111", keys);
        Assert.DoesNotContain("378282246310005", keys);
        Assert.DoesNotContain("4111111111111112", keys); // the NUMERIC key masks too
        Assert.Contains(LogScrubbingEnricher.MaskToken, keys);
        Assert.Contains($"{LogScrubbingEnricher.MaskToken} #2", keys);
        Assert.Contains($"{LogScrubbingEnricher.MaskToken} #3", keys);
        Assert.Contains("channel", keys);
    }

    [Fact]
    public void DictionaryKeysUnderCredentialNamedProperty_AreMaskedWholesale()
    {
        // Under a credential-named property the dictionary KEYS are part of the
        // secret payload (e.g. a token -> label map), so the sensitive-subtree flag
        // masks them wholesale — content-shape detection alone would let a digitless
        // secret key print verbatim. Dedupe keeps the two masked keys distinct.
        var dict = new DictionaryValue(
        [
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                new ScalarValue("sk-live-abc"), new ScalarValue("primary")),
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                new ScalarValue("sk-live-def"), new ScalarValue("backup")),
        ]);
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("test"),
            [new LogEventProperty("ProviderCredentials", dict)]);

        new LogScrubbingEnricher().Enrich(logEvent, null!);

        var scrubbed = (DictionaryValue)logEvent.Properties["ProviderCredentials"];
        var keys = scrubbed.Elements.Keys.Select(k => (string?)k.Value).ToArray();
        Assert.DoesNotContain("sk-live-abc", keys);
        Assert.DoesNotContain("sk-live-def", keys);
        Assert.Contains(LogScrubbingEnricher.MaskToken, keys);
        Assert.Contains($"{LogScrubbingEnricher.MaskToken} #2", keys);
        Assert.All(
            scrubbed.Elements.Values,
            v => Assert.Equal(LogScrubbingEnricher.MaskToken, ((ScalarValue)v).Value));
    }

    [Fact]
    public void CredentialNamedDictionaryKey_MasksTheValue_KeyTextSurvives()
    {
        // A key like "password" is a NAME, not secret content: it drives wholesale
        // masking of its VALUE, but the key text itself must survive so the log still
        // shows WHAT was masked.
        var dict = new DictionaryValue(
        [
            new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                new ScalarValue("password"), new ScalarValue("hunter2")),
        ]);
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("test"),
            [new LogEventProperty("Settings", dict)]);

        new LogScrubbingEnricher().Enrich(logEvent, null!);

        var scrubbed = (DictionaryValue)logEvent.Properties["Settings"];
        var (key, element) = scrubbed.Elements.Single();
        Assert.Equal("password", key.Value);
        Assert.Equal(LogScrubbingEnricher.MaskToken, ((ScalarValue)element).Value);
    }

    [Fact]
    public void BenignProperties_AreLeftUntouched()
    {
        var logEvent = MakeEvent(
            ("TenantId", "tenant-a"),
            ("PartySize", 4),
            ("Reference", "5260181590830166")); // no embedded Luhn window — must survive

        new LogScrubbingEnricher().Enrich(logEvent, null!);

        Assert.Equal("tenant-a", GetString(logEvent, "TenantId"));
        Assert.Equal("5260181590830166", GetString(logEvent, "Reference"));
        Assert.Equal(4, (logEvent.Properties["PartySize"] as ScalarValue)?.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankConfiguredField_DoesNotMaskEveryProperty(string blank)
    {
        // Bug 1: IsSensitiveName uses substring Contains, and name.Contains("") is ALWAYS true. A
        // single empty/whitespace configured field would make EVERY property match and mask every
        // log line — a self-inflicted outage. Blank entries must be dropped before merging.
        var logEvent = MakeEvent(("TenantId", "tenant-a"), ("PartySize", 4));

        new LogScrubbingEnricher([blank]).Enrich(logEvent, null!);

        Assert.Equal("tenant-a", GetString(logEvent, "TenantId"));
        Assert.Equal(4, (logEvent.Properties["PartySize"] as ScalarValue)?.Value);
    }

    [Fact]
    public void ConfiguredSensitiveField_IsHonored_AlongsideTheBaseline()
    {
        // A real (non-blank) configured selector still merges with the baseline and masks by name.
        var logEvent = MakeEvent(("SessionCookie", "abc123"), ("TenantId", "tenant-a"));

        new LogScrubbingEnricher(["cookie"]).Enrich(logEvent, null!);

        Assert.Equal(LogScrubbingEnricher.MaskToken, GetString(logEvent, "SessionCookie"));
        Assert.Equal("tenant-a", GetString(logEvent, "TenantId")); // untouched — selector is specific
    }
}
