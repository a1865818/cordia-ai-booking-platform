using System.Globalization;
using Cordia.Domain.Security;
using Serilog.Core;
using Serilog.Events;

namespace Cordia.Api.Logging;

/// <summary>
/// Serilog enricher enforcing Inv-12/§24.4 on every log event: string property
/// values are masked when (a) the property NAME matches a credential-like
/// selector (password/secret/token/…) or (b) the VALUE contains a Luhn-valid
/// PAN shape (delegating to <see cref="SensitiveDataMask"/> so both write
/// paths mask identically). Applied globally in Program.cs.
///
/// Scope note: enrichers cannot rewrite an attached Exception object — the
/// complementary controls are the <c>ISecretStore</c> contract (secret values
/// are never placed in exception messages) and the GlobalExceptionHandler
/// (unmapped exceptions return a generic 500 with no internals).
/// </summary>
public sealed class LogScrubbingEnricher : ILogEventEnricher
{
    public const string MaskToken = SensitiveDataMask.MaskToken;

    // NOTE: matched as substrings of the property NAME. Deliberately no bare
    // "pan" fragment — it over-matches SpanId/CompanyName/etc.; PAN VALUES are
    // caught by the value-level SensitiveDataMask sweep below regardless of
    // the property name. "token" intentionally over-matches (Access/Refresh/
    // continuation tokens are all credential-adjacent — fail safe).
    private static readonly string[] BaselineNameFragments =
    [
        "password", "secret", "token", "apikey", "api_key",
        "authorization", "credential", "cvv", "card_number",
    ];

    private readonly string[] _sensitiveNameFragments;

    /// <param name="configuredSensitiveFields">
    /// Deployment/tenant-configured sensitive property-name selectors
    /// (spec 14 §4.1 RedactionPolicy.configured_sensitive_fields — the API-side
    /// loading seam, bound from the <c>Redaction:SensitiveFields</c>
    /// configuration section in Program.cs). Merged with the always-on baseline.
    /// </param>
    public LogScrubbingEnricher(IReadOnlyList<string>? configuredSensitiveFields = null)
    {
        // Drop blank/whitespace entries first: IsSensitiveName uses substring Contains, and
        // "".Contains("") / name.Contains("") is ALWAYS true — a single empty configured field
        // would make EVERY property match and mask every log line (a self-inflicted outage).
        var configured = configuredSensitiveFields?
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .ToArray();
        _sensitiveNameFragments = configured is { Length: > 0 }
            ? [.. BaselineNameFragments, .. configured]
            : BaselineNameFragments;
    }

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var (name, value) in logEvent.Properties.ToList())
        {
            var scrubbed = ScrubValue(name, value);
            if (!ReferenceEquals(scrubbed, value))
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, scrubbed));
        }
    }

    /// <summary>
    /// Recursively scrubs a property value — destructured objects
    /// (<see cref="StructureValue"/>), arrays (<see cref="SequenceValue"/>),
    /// and dictionaries are walked so a PAN or credential nested inside a
    /// <c>{@Object}</c> log call cannot bypass the mask (Inv-12). A
    /// credential-like name poisons the WHOLE subtree (<paramref name="sensitiveContext"/>):
    /// <c>logger.Information("{@Password}", new { Value = "hunter2" })</c> must mask
    /// the nested string even though the child's own name ("Value") is innocuous.
    /// Returns the SAME instance when nothing changed (cheap no-op path).
    /// </summary>
    private LogEventPropertyValue ScrubValue(string name, LogEventPropertyValue value, bool sensitiveContext = false)
    {
        var sensitive = sensitiveContext || IsSensitiveName(name);
        switch (value)
        {
            case ScalarValue scalar:
                // A sensitive name/subtree masks EVERY non-null scalar, not just strings —
                // {@Secret} destructured to new { Value = 4111111111111111L } must not leak
                // the numeric form (Inv-12 fail-closed; null stays null: the mask token there
                // would fabricate a value that never existed).
                if (sensitive)
                    return scalar.Value is null ? value : new ScalarValue(MaskToken);
                if (scalar.Value is string text)
                {
                    var scrubbed = SensitiveDataMask.Sanitize(text);
                    return scrubbed == text ? value : new ScalarValue(scrubbed);
                }

                // Numeric PAN parity with SanitizeJson's numeric branch: {Card} logged as the
                // NUMBER 4111111111111111L must mask like the string form. Only these types can
                // render 13+ digits (int/uint cap at 10; Serilog stringifies unknown types like
                // BigInteger, so the string branch above catches those). Below 1e12 nothing can
                // render a 13-digit contiguous run (a float/double fraction renders 6 digits max
                // under F6 and '.' breaks the run), so timers/ids/totals skip the format+scan on
                // this hot path; decimal stays ungated — its fraction alone can carry a 13+ digit
                // run at any magnitude. Floats format fixed-point (default ToString goes
                // scientific past ~1e15 and the digits would evade detection — same trap the
                // Python lane fixed); a float/double past 2^53 lost the exact digits BEFORE
                // reaching this enricher, so detection operates on the rounded rendering, and a
                // Luhn-coincidental large numeric id over-masks fail-closed exactly like the
                // string lane (Inv-12 recall-over-precision, the accepted posture). Anything
                // maskable replaces the WHOLE scalar with the token, never a partial value.
                var raw = scalar.Value switch
                {
                    long and > -1_000_000_000_000 and < 1_000_000_000_000 => null,
                    ulong and < 1_000_000_000_000UL => null,
                    double and > -1e12 and < 1e12 => null,
                    float and > -1e12f and < 1e12f => null,
                    long or ulong or decimal =>
                        Convert.ToString(scalar.Value, CultureInfo.InvariantCulture),
                    double d => d.ToString("F6", CultureInfo.InvariantCulture),
                    float f => f.ToString("F6", CultureInfo.InvariantCulture),
                    _ => null,
                };
                if (raw is not null && SensitiveDataMask.Sanitize(raw) != raw)
                    return new ScalarValue(MaskToken);
                return value;

            case StructureValue structure:
                {
                    var changed = false;
                    var properties = new List<LogEventProperty>(structure.Properties.Count);
                    foreach (var property in structure.Properties)
                    {
                        var inner = ScrubValue(property.Name, property.Value, sensitive);
                        changed |= !ReferenceEquals(inner, property.Value);
                        properties.Add(new LogEventProperty(property.Name, inner));
                    }
                    return changed ? new StructureValue(properties, structure.TypeTag) : value;
                }

            case SequenceValue sequence:
                {
                    var changed = false;
                    var elements = new List<LogEventPropertyValue>(sequence.Elements.Count);
                    foreach (var element in sequence.Elements)
                    {
                        var inner = ScrubValue(name, element, sensitive);
                        changed |= !ReferenceEquals(inner, element);
                        elements.Add(inner);
                    }
                    return changed ? new SequenceValue(elements) : value;
                }

            case DictionaryValue dictionary:
                {
                    var changed = false;
                    var elements = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(
                        dictionary.Elements.Count);
                    foreach (var (key, element) in dictionary.Elements)
                    {
                        var keyName = key.Value as string ?? name;
                        var inner = ScrubValue(keyName, element, sensitive);
                        changed |= !ReferenceEquals(inner, element);
                        elements.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(key, inner));
                    }
                    return changed ? new DictionaryValue(elements) : value;
                }

            default:
                return value;
        }
    }

    private bool IsSensitiveName(string name)
    {
        foreach (var fragment in _sensitiveNameFragments)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
