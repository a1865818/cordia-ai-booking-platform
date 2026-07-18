using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace Cordia.Infrastructure.Observability;

/// <summary>
/// Default-deny redaction for structured-logged objects (§25.3, Inv-12). When a complex object is
/// destructured (logged with the <c>@</c> operator), any property whose <b>name</b> matches the
/// sensitive denylist — or any <b>string value</b> matching a PAN pattern regardless of field name —
/// is replaced with <see cref="RedactedMarker"/> before it can reach any sink. This keeps secrets,
/// PAN/CVV, and raw transcript/audio content out of logs, traces, and metrics (Inv-12).
///
/// <para><b>Limitation (by design).</b> A Serilog destructuring policy only fires for values logged
/// with the <c>@</c> destructuring operator (<c>{@Payload}</c>). A sensitive object interpolated by
/// its <c>ToString()</c> (<c>{Payload}</c>) bypasses this policy. The convention is therefore:
/// ALWAYS log domain/payload objects with <c>@</c>, and never put a secret/PAN/transcript directly
/// into a message-template scalar. Value-level PAN masking here is a backstop, not the primary guard.
/// See <c>RedactionTests.LoggingWithoutDestructuring_BypassesPolicy_DocumentsConvention</c>.</para>
/// </summary>
public sealed partial class RedactingDestructuringPolicy : IDestructuringPolicy
{
    /// <summary>The placeholder written in place of a redacted value.</summary>
    public const string RedactedMarker = "***REDACTED***";

    /// <summary>
    /// Case-insensitive substring denylist of sensitive property names (§25.3). Substring match so
    /// e.g. <c>cardNumber</c>, <c>api_key</c>, <c>messageContent</c> are all caught.
    /// </summary>
    private static readonly string[] SensitiveNameFragments =
    [
        "password", "secret", "token", "apikey", "api_key", "authorization", "auth",
        "credential", "pan", "card", "cardnumber", "card_number", "cvv", "cvc", "ccv",
        "pin", "ssn", "transcript", "audio", "prompt", "completion", "message_content",
        "raw_content",
    ];

    // 13–19 consecutive digits, optionally separated by single spaces or dashes (PAN / card number).
    [GeneratedRegex(@"\b(?:\d[ -]?){13,19}\b", RegexOptions.CultureInvariant)]
    private static partial Regex PanRegex();

    /// <inheritdoc />
    public bool TryDestructure(
        object value,
        ILogEventPropertyValueFactory propertyValueFactory,
        out LogEventPropertyValue result)
    {
        var type = value.GetType();

        // Only handle plain complex objects; leave scalars/collections/strings/enumerables to Serilog's
        // defaults — intercepting IEnumerable would strip the actual elements, leaving only metadata properties.
        if (type.IsPrimitive || value is string || type.IsEnum || value is System.Collections.IEnumerable)
        {
            result = null!;
            return false;
        }

        var props = type.GetProperties(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (props.Length == 0)
        {
            result = null!;
            return false;
        }

        var members = new List<LogEventProperty>(props.Length);
        foreach (var prop in props)
        {
            if (prop.GetIndexParameters().Length > 0)
                continue;

            object? raw;
            try
            {
                raw = prop.GetValue(value);
            }
            catch
            {
                // Fail-open: never throw on the business path because of redaction.
                continue;
            }

            members.Add(new LogEventProperty(prop.Name, Redact(prop.Name, raw, propertyValueFactory)));
        }

        result = new StructureValue(members, type.Name);
        return true;
    }

    private static LogEventPropertyValue Redact(
        string name,
        object? raw,
        ILogEventPropertyValueFactory propertyValueFactory)
    {
        if (IsSensitiveName(name))
            return new ScalarValue(RedactedMarker);

        if (raw is string s && PanRegex().IsMatch(s))
            return new ScalarValue(RedactedMarker);

        // Non-sensitive: destructure normally (recursing this policy for nested complex objects).
        return propertyValueFactory.CreatePropertyValue(raw, destructureObjects: true);
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
