using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Cordia.Api.Security;

/// <summary>
/// HMAC signatures for provider webhooks (#74, decision D2).
///
/// <para><b>The wire format.</b> The provider sends two headers:</para>
/// <list type="bullet">
///   <item><c>X-Webhook-Timestamp</c> — Unix seconds when it signed the request.</item>
///   <item><c>X-Webhook-Signature</c> — <c>sha256=</c> followed by the lowercase hex
///   HMAC-SHA256, keyed by the shared webhook secret, of the UTF-8 bytes of
///   <c>"{timestamp}.{tenant_id}."</c> followed by the raw request body bytes.</item>
/// </list>
///
/// <para><b>Why each part is signed.</b> The plain <c>X-Webhook-Secret</c> header this replaces
/// proved only that the caller knew a value: anyone who saw one request could forge any receipt, and
/// the body could be altered in flight. The body makes the receipt itself tamper-evident. The
/// timestamp, checked against a <see cref="Tolerance"/> window, bounds how long a captured request
/// can be replayed. The tenant is signed because it arrives in a header, not the body — without it a
/// captured receipt could be replayed against another tenant.</para>
///
/// <para>Deliberately not a JWS (<c>JwsTenantContextSigner</c>'s shape): webhook providers sign a
/// header over the body they send, and the body must stay the plain JSON the endpoint documents.</para>
/// </summary>
public static class WebhookSignature
{
    public const string TimestampHeader = "X-Webhook-Timestamp";
    public const string SignatureHeader = "X-Webhook-Signature";
    public const string SchemePrefix = "sha256=";

    /// <summary>How far a signing timestamp may be from the server clock, in either direction.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    /// <summary>The header value a provider sends for this request.</summary>
    public static string Compute(string secret, string timestamp, string tenantId, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes($"{timestamp}.{tenantId}.");
        var message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(prefix.Length));

        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message);
        return SchemePrefix + Convert.ToHexString(mac).ToLowerInvariant();
    }

    /// <summary>
    /// True only when the timestamp is well-formed and inside <see cref="Tolerance"/> of
    /// <paramref name="now"/>, and the signature matches in constant time.
    /// </summary>
    public static bool Verify(
        string secret,
        string? timestamp,
        string? signature,
        string tenantId,
        ReadOnlySpan<byte> body,
        DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature))
            return false;

        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return false;

        DateTimeOffset signedAt;
        try
        {
            signedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        if ((now - signedAt).Duration() > Tolerance)
            return false;

        var expected = Encoding.ASCII.GetBytes(Compute(secret, timestamp, tenantId, body));
        var provided = Encoding.ASCII.GetBytes(signature);

        // Equal-length digests only reach FixedTimeEquals; a wrong length is wrong regardless, and its
        // length is the public length of a SHA-256 hex digest, so returning early leaks nothing.
        return expected.Length == provided.Length && CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}
