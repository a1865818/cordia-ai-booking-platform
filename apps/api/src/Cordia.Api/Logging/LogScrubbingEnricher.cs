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

    private static readonly string[] SensitiveNameFragments =
    [
        "password", "secret", "token", "apikey", "api_key",
        "authorization", "credential", "pan", "cvv", "card_number",
    ];

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var (name, value) in logEvent.Properties.ToList())
        {
            if (value is not ScalarValue { Value: string text })
                continue;

            if (IsSensitiveName(name))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, new ScalarValue(MaskToken)));
                continue;
            }

            var scrubbed = SensitiveDataMask.Sanitize(text);
            if (!ReferenceEquals(scrubbed, text) && scrubbed != text)
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, new ScalarValue(scrubbed)));
        }
    }

    private static bool IsSensitiveName(string name)
    {
        foreach (var fragment in SensitiveNameFragments)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
