using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.TenantResolution;

namespace Cordia.Infrastructure.TenantResolution;

/// <summary>
/// HS256 compact-JWS <see cref="IOperatorSessionSigner"/> (G1 web-origin auth).
///
/// Wire form matches <see cref="JwsTenantContextSigner"/> byte-for-byte in the
/// canonical-JSON + HMAC-SHA256 sense: <c>base64url(header).base64url(payload).base64url(signature)</c>
/// where <c>header = {"alg":"HS256","kid":&lt;activeKid&gt;,"typ":"opctx+jws"}</c>, payload is
/// canonical JSON of <see cref="OperatorSessionClaims"/> (sorted keys, no whitespace,
/// <c>ensure_ascii</c>), and the signature is HMAC-SHA256 over the ASCII signing input.
/// Keys are loaded from the same <see cref="ISecretStore"/> custody seam as tenant context
/// (<c>TenantContextSigningKeys</c>) and are never logged or serialized.
/// </summary>
public sealed class JwsOperatorSessionSigner : IOperatorSessionSigner
{
    private const string ConfigPrefix = "TenantContextSigningKeys";
    private const string Alg = "HS256";
    private const string Typ = "opctx+jws";
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions PayloadParseOptions = new();

    // Signing key material — kid -> HMAC secret bytes. Never logged or serialized.
    private readonly IReadOnlyDictionary<string, byte[]> _keys;
    private readonly string _activeKid;
    private readonly TimeProvider _timeProvider;

    /// <summary>DI constructor — real wall clock.</summary>
    public JwsOperatorSessionSigner(ISecretStore secrets)
        : this(secrets, TimeProvider.System)
    {
    }

    /// <summary>Testable constructor — inject a pinned <see cref="TimeProvider"/> for skew/expiry tests.</summary>
    public JwsOperatorSessionSigner(ISecretStore secrets, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        _timeProvider = timeProvider;

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
            throw new InvalidOperationException(
                "TenantContext signing configuration is invalid: the active key id is not in the active key set.");
        }

        _keys = keys;
    }

    public string Sign(OperatorSessionClaims claims)
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

    public OperatorSessionClaims Verify(string jws)
    {
        if (string.IsNullOrEmpty(jws))
            throw Malformed();

        var parts = jws.Split('.');
        if (parts.Length != 3)
            throw Malformed();

        var headerB64 = parts[0];
        var payloadB64 = parts[1];
        var signatureB64 = parts[2];

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

        if (!TryGetHeaderString(header, "alg", out var alg) || !string.Equals(alg, Alg, StringComparison.Ordinal))
            throw new TenantContextVerificationException(
                "Operator session token uses an unsupported signing algorithm.");

        if (!TryGetHeaderString(header, "kid", out var kid) || !_keys.TryGetValue(kid, out var key))
            throw new TenantContextVerificationException("Operator session token key is not recognised.");

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

        if (!TryGetHeaderString(header, "typ", out var typ) || !string.Equals(typ, Typ, StringComparison.Ordinal))
            throw new TenantContextVerificationException("Operator session token has an unexpected type.");

        var now = _timeProvider.GetUtcNow();
        if (now >= dto.ExpiresAt)
            throw new TenantContextVerificationException("Operator session token has expired.");
        if (dto.IssuedAt > now + ClockSkew)
            throw new TenantContextVerificationException("Operator session token issued-at is in the future.");

        return dto.ToClaims();
    }

    private static TenantContextVerificationException Malformed()
        => new("Operator session token is malformed.");

    private static TenantContextVerificationException SignatureFailed()
        => new("Operator session signature verification failed.");

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

    private static string BuildCanonicalHeader(string kid)
    {
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

    private static string BuildCanonicalPayload(OperatorSessionClaims c)
    {
        // Keys in lexicographic order, no whitespace.
        var sb = new StringBuilder();
        sb.Append('{');
        AppendKey(sb, "actor_id");
        AppendJsonString(sb, c.ActorId);
        sb.Append(',');
        AppendKey(sb, "actor_role");
        AppendJsonString(sb, c.ActorRole);
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

    /// <summary>
    /// Escapes <paramref name="value"/> exactly as CPython's <c>json.dumps(..., ensure_ascii=True)</c>
    /// (copied from <see cref="JwsTenantContextSigner"/>).
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

    private static string FormatTimestamp(DateTimeOffset value)
    {
        var sb = new StringBuilder();
        sb.Append(value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));

        var micros = value.Ticks % TimeSpan.TicksPerSecond / 10;
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

    private sealed class ClaimsDto
    {
        [JsonPropertyName("tenant_id")] public string? TenantId { get; set; }
        [JsonPropertyName("actor_id")] public string? ActorId { get; set; }
        [JsonPropertyName("actor_role")] public string? ActorRole { get; set; }
        [JsonPropertyName("session_id")] public string? SessionId { get; set; }
        [JsonPropertyName("issued_at")] public DateTimeOffset IssuedAt { get; set; }
        [JsonPropertyName("expires_at")] public DateTimeOffset ExpiresAt { get; set; }

        public OperatorSessionClaims ToClaims()
        {
            if (TenantId is null || ActorId is null || ActorRole is null || SessionId is null)
                throw Malformed();

            return new OperatorSessionClaims(
                TenantId,
                ActorId,
                ActorRole,
                SessionId,
                IssuedAt,
                ExpiresAt);
        }
    }
}
