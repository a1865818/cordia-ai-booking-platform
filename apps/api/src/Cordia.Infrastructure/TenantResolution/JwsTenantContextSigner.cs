using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.TenantResolution;

namespace Cordia.Infrastructure.TenantResolution;

/// <summary>
/// HS256 compact-JWS <see cref="ITenantContextSigner"/> (spec-15 Phase 2).
///
/// Wire form (frozen, shared byte-for-byte with the langgraph verifier —
/// <c>apps/langgraph/tests/_stubs/tenant_context_stub.py</c>):
/// <c>base64url(header).base64url(payload).base64url(signature)</c> where
/// <c>header = {"alg":"HS256","kid":&lt;activeKid&gt;,"typ":"tenantctx+jws"}</c>,
/// <c>payload</c> is canonical JSON of the claim set (sorted keys, no whitespace,
/// <c>ensure_ascii</c> — non-ASCII escaped as <c>\uXXXX</c>, matching Python's
/// <c>json.dumps(sort_keys=True, separators=(",",":"), ensure_ascii=True)</c>), and the
/// signature is <c>HMAC-SHA256</c> over the ASCII signing input
/// <c>base64url(header) + "." + base64url(payload)</c>. base64url is URL-safe, no padding.
///
/// Keys are loaded once at construction from <see cref="ISecretStore"/> (the custody seam)
/// and kept only in a private field. They are never logged, serialized, or placed in an
/// exception message.
/// </summary>
public sealed class JwsTenantContextSigner : ITenantContextSigner
{
    private const string ConfigPrefix = "TenantContextSigningKeys";
    private const string Alg = "HS256";
    private const string Typ = "tenantctx+jws";
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions PayloadParseOptions = new();

    // Signing key material — kid -> HMAC secret bytes. Never logged or serialized.
    private readonly IReadOnlyDictionary<string, byte[]> _keys;
    private readonly string _activeKid;
    private readonly TimeProvider _timeProvider;

    /// <summary>DI constructor — real wall clock.</summary>
    public JwsTenantContextSigner(ISecretStore secrets)
        : this(secrets, TimeProvider.System)
    {
    }

    /// <summary>Testable constructor — inject a pinned <see cref="TimeProvider"/> for skew/expiry tests.</summary>
    public JwsTenantContextSigner(ISecretStore secrets, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _timeProvider = timeProvider;

        // Every piece of key material flows through the ISecretStore custody seam.
        _activeKid = secrets.GetRequiredSecret($"{ConfigPrefix}:ActiveKid");
        var kidList = secrets.GetRequiredSecret($"{ConfigPrefix}:KeyIds")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var kid in kidList)
        {
            var base64Secret = secrets.GetRequiredSecret($"{ConfigPrefix}:Keys:{kid}");
            keys[kid] = Convert.FromBase64String(base64Secret);
        }

        if (!keys.ContainsKey(_activeKid))
        {
            // No key value in the message — configuration error only.
            throw new InvalidOperationException(
                "TenantContext signing configuration is invalid: the active key id is not in the active key set.");
        }

        _keys = keys;
    }

    public string Sign(TenantContextClaims claims)
    {
        ArgumentNullException.ThrowIfNull(claims);

        var header = BuildCanonicalHeader(_activeKid);
        var payload = BuildCanonicalPayload(claims);

        var headerB64 = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header));
        var payloadB64 = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));
        var signingInput = $"{headerB64}.{payloadB64}";

        var signature = ComputeSignature(_keys[_activeKid], signingInput);
        var signatureB64 = Base64Url.EncodeToString(signature);

        return $"{signingInput}.{signatureB64}";
    }

    public TenantContextClaims Verify(string jws)
    {
        if (string.IsNullOrEmpty(jws))
            throw Malformed();

        var parts = jws.Split('.');
        if (parts.Length != 3)
            throw Malformed();

        var headerB64 = parts[0];
        var payloadB64 = parts[1];
        var signatureB64 = parts[2];

        // Parse header.
        JsonElement header;
        try
        {
            using var doc = JsonDocument.Parse(Base64Url.DecodeFromChars(headerB64));
            header = doc.RootElement.Clone();
        }
        catch
        {
            throw Malformed();
        }
        if (header.ValueKind != JsonValueKind.Object)
            throw Malformed();

        // Algorithm must be HS256 (guards algorithm-confusion attacks).
        if (!TryGetHeaderString(header, "alg", out var alg) || !string.Equals(alg, Alg, StringComparison.Ordinal))
            throw new TenantContextVerificationException(
                "Tenant context token uses an unsupported signing algorithm.");

        // kid must be present and in the active key set (rejects unknown/retired keys).
        if (!TryGetHeaderString(header, "kid", out var kid) || !_keys.TryGetValue(kid, out var key))
            throw new TenantContextVerificationException("Tenant context token key is not recognised.");

        // Recompute the signature over the RECEIVED signing input and compare in constant time.
        var expected = ComputeSignature(key, $"{headerB64}.{payloadB64}");
        byte[] provided;
        try
        {
            provided = Base64Url.DecodeFromChars(signatureB64);
        }
        catch
        {
            throw SignatureFailed();
        }
        if (!CryptographicOperations.FixedTimeEquals(expected, provided))
            throw SignatureFailed();

        // Signature is valid — parse the now-trusted payload.
        ClaimsDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ClaimsDto>(Base64Url.DecodeFromChars(payloadB64), PayloadParseOptions);
        }
        catch
        {
            throw Malformed();
        }
        if (dto is null)
            throw Malformed();

        // typ must match.
        if (!TryGetHeaderString(header, "typ", out var typ) || !string.Equals(typ, Typ, StringComparison.Ordinal))
            throw new TenantContextVerificationException("Tenant context token has an unexpected type.");

        // Temporal validity: not expired, and issued_at not in the future beyond the skew.
        var now = _timeProvider.GetUtcNow();
        if (now >= dto.ExpiresAt)
            throw new TenantContextVerificationException("Tenant context token has expired.");
        if (dto.IssuedAt > now + ClockSkew)
            throw new TenantContextVerificationException("Tenant context token issued-at is in the future.");

        return dto.ToClaims();
    }

    private static TenantContextVerificationException Malformed()
        => new("Tenant context token is malformed.");

    private static TenantContextVerificationException SignatureFailed()
        => new("Tenant context signature verification failed.");

    private static byte[] ComputeSignature(byte[] key, string signingInput)
        => HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(signingInput));

    private static bool TryGetHeaderString(JsonElement obj, string name, out string value)
    {
        if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString()!;
            return true;
        }
        value = string.Empty;
        return false;
    }

    // --- Canonical JSON (matches Python json.dumps(sort_keys, separators=(",",":"), ensure_ascii)) ---

    private static string BuildCanonicalHeader(string kid)
    {
        // Keys already alphabetical: alg < kid < typ.
        var sb = new StringBuilder();
        sb.Append('{');
        AppendJsonString(sb, "alg");
        sb.Append(':');
        AppendJsonString(sb, Alg);
        sb.Append(',');
        AppendJsonString(sb, "kid");
        sb.Append(':');
        AppendJsonString(sb, kid);
        sb.Append(',');
        AppendJsonString(sb, "typ");
        sb.Append(':');
        AppendJsonString(sb, Typ);
        sb.Append('}');
        return sb.ToString();
    }

    private static string BuildCanonicalPayload(TenantContextClaims c)
    {
        // Keys in lexicographic order, no whitespace.
        var sb = new StringBuilder();
        sb.Append('{');
        AppendKey(sb, "allowed_agent_policy_ids");
        AppendStringArray(sb, c.AllowedAgentPolicyIds);
        sb.Append(',');
        AppendKey(sb, "allowed_tool_policy_ids");
        AppendStringArray(sb, c.AllowedToolPolicyIds);
        sb.Append(',');
        AppendKey(sb, "channel");
        AppendJsonString(sb, ChannelToWire(c.Channel));
        sb.Append(',');
        AppendKey(sb, "channel_endpoint_id");
        AppendJsonString(sb, c.ChannelEndpointId);
        sb.Append(',');
        AppendKey(sb, "configuration_version");
        AppendJsonString(sb, c.ConfigurationVersion);
        sb.Append(',');
        AppendKey(sb, "correlation_id");
        AppendJsonString(sb, c.CorrelationId);
        sb.Append(',');
        AppendKey(sb, "expires_at");
        AppendJsonString(sb, FormatTimestamp(c.ExpiresAt));
        sb.Append(',');
        AppendKey(sb, "issued_at");
        AppendJsonString(sb, FormatTimestamp(c.IssuedAt));
        sb.Append(',');
        AppendKey(sb, "session_id");
        AppendJsonString(sb, c.SessionId);
        sb.Append(',');
        AppendKey(sb, "tenant_id");
        AppendJsonString(sb, c.TenantId);
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendKey(StringBuilder sb, string key)
    {
        AppendJsonString(sb, key);
        sb.Append(':');
    }

    private static void AppendStringArray(StringBuilder sb, IReadOnlyList<string> items)
    {
        sb.Append('[');
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            AppendJsonString(sb, items[i]);
        }
        sb.Append(']');
    }

    /// <summary>
    /// Escapes <paramref name="value"/> exactly as CPython's <c>json.dumps(..., ensure_ascii=True)</c>:
    /// backslash/quote and the five short control escapes, all other control chars and every
    /// non-ASCII char (&gt; 0x7e) as lowercase <c>\uXXXX</c> (astral chars become UTF-16 surrogate
    /// pairs). No built-in .NET encoder matches this (the defaults also escape &lt; &gt; &amp; ' + ),
    /// so this is hand-rolled.
    /// </summary>
    private static void AppendJsonString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (ch < 0x20 || ch > 0x7e)
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
    }

    private static string ChannelToWire(TenantChannel channel) => channel switch
    {
        TenantChannel.Voice => "voice",
        TenantChannel.Messaging => "messaging",
        TenantChannel.Web => "web",
        TenantChannel.Email => "email",
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unsupported tenant channel."),
    };

    /// <summary>
    /// Formats a timestamp to match pydantic v2 <c>model_dump(mode="json")</c> for an
    /// <c>AwareDatetime</c>: UTC → trailing <c>Z</c>; other offsets → <c>+HH:MM</c>; microsecond
    /// precision, fractional part omitted when zero.
    /// </summary>
    private static string FormatTimestamp(DateTimeOffset value)
    {
        var sb = new StringBuilder();
        sb.Append(value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));

        var micros = value.Ticks % TimeSpan.TicksPerSecond / 10; // 100ns ticks -> microseconds (truncated)
        if (micros != 0)
            sb.Append('.').Append(micros.ToString("D6", CultureInfo.InvariantCulture));

        var offset = value.Offset;
        if (offset == TimeSpan.Zero)
        {
            sb.Append('Z');
        }
        else
        {
            sb.Append(offset < TimeSpan.Zero ? '-' : '+');
            var abs = offset.Duration();
            sb.Append(abs.Hours.ToString("D2", CultureInfo.InvariantCulture));
            sb.Append(':');
            sb.Append(abs.Minutes.ToString("D2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>Deserialization shape for the trusted payload; mapped to <see cref="TenantContextClaims"/>.</summary>
    private sealed class ClaimsDto
    {
        [JsonPropertyName("tenant_id")] public string? TenantId { get; set; }
        [JsonPropertyName("channel")] public TenantChannel Channel { get; set; }
        [JsonPropertyName("channel_endpoint_id")] public string? ChannelEndpointId { get; set; }
        [JsonPropertyName("configuration_version")] public string? ConfigurationVersion { get; set; }
        [JsonPropertyName("correlation_id")] public string? CorrelationId { get; set; }
        [JsonPropertyName("session_id")] public string? SessionId { get; set; }
        [JsonPropertyName("allowed_agent_policy_ids")] public List<string>? AllowedAgentPolicyIds { get; set; }
        [JsonPropertyName("allowed_tool_policy_ids")] public List<string>? AllowedToolPolicyIds { get; set; }
        [JsonPropertyName("issued_at")] public DateTimeOffset IssuedAt { get; set; }
        [JsonPropertyName("expires_at")] public DateTimeOffset ExpiresAt { get; set; }

        public TenantContextClaims ToClaims()
        {
            if (TenantId is null || ChannelEndpointId is null || ConfigurationVersion is null
                || CorrelationId is null || SessionId is null
                || AllowedAgentPolicyIds is null || AllowedToolPolicyIds is null)
            {
                throw Malformed();
            }

            return new TenantContextClaims(
                TenantId,
                Channel,
                ChannelEndpointId,
                ConfigurationVersion,
                CorrelationId,
                SessionId,
                AllowedAgentPolicyIds,
                AllowedToolPolicyIds,
                IssuedAt,
                ExpiresAt);
        }
    }
}
