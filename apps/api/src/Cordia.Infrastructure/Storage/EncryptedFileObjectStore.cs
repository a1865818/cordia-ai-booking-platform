using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Cordia.Domain.Interfaces;
using Microsoft.Extensions.Configuration;

namespace Cordia.Infrastructure.Storage;

/// <summary>
/// Filesystem <see cref="IObjectStore"/> with AES-256-GCM encryption at rest (spec 14 §24.4,
/// gate decision 2026-07-10). Layout: <c>{root}/{tenant}/{purpose}/{id}.bin</c> holding
/// <c>nonce(12) ‖ tag(16) ‖ ciphertext</c>, plus a plaintext <c>{id}.meta.json</c> sidecar
/// (created_at / expires_at / purpose — control-plane only, never content). The 256-bit key is
/// resolved LAZILY from <c>ISecretStore.GetRequiredSecret("ObjectStoreEncryptionKey")</c>
/// (base64) so a missing key fails on USE with <c>SecretNotFoundException</c>, not at startup.
/// Root comes from <c>ObjectStore:Root</c>; raw-audio objects default to
/// <c>ObjectStore:RawAudioTtlHours</c> (72) when the caller passes no TTL. Path segments are
/// allow-list validated (traversal-proof) before any filesystem access.
/// </summary>
public sealed partial class EncryptedFileObjectStore(
    ISecretStore secrets,
    IConfiguration config) : IObjectStore
{
    public const string EncryptionKeySecretName = "ObjectStoreEncryptionKey";
    private const string RawAudioPurpose = "raw-audio";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    // Conservative allow-list for tenant/purpose/id path segments: no separators, no dots
    // except inside the id body (never leading), so ".." and absolute paths cannot form.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex SafeSegment();

    private sealed record ObjectMeta(
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
        [property: JsonPropertyName("purpose")] string Purpose);

    private string Root => config["ObjectStore:Root"]
        ?? Path.Combine(Directory.GetCurrentDirectory(), ".objectstore");

    private double RawAudioTtlHours => config.GetValue("ObjectStore:RawAudioTtlHours", 72d);

    public async Task PutAsync(
        string tenantId,
        string purpose,
        string objectId,
        ReadOnlyMemory<byte> content,
        TimeSpan? ttl = null,
        CancellationToken ct = default)
    {
        var path = ResolvePath(tenantId, purpose, objectId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var now = DateTimeOffset.UtcNow;
        // Fail closed on retention: raw audio never persists without a TTL (gate: 72h default).
        var effectiveTtl = ttl ?? (purpose == RawAudioPurpose ? TimeSpan.FromHours(RawAudioTtlHours) : null);

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var ciphertext = new byte[content.Length];
        using (var aes = new AesGcm(ResolveKey(), TagSize))
        {
            aes.Encrypt(nonce, content.Span, ciphertext, tag);
        }

        var blob = new byte[NonceSize + TagSize + ciphertext.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceSize);
        ciphertext.CopyTo(blob, NonceSize + TagSize);

        await File.WriteAllBytesAsync(path, blob, ct);
        await File.WriteAllTextAsync(
            MetaPath(path),
            JsonSerializer.Serialize(new ObjectMeta(now, effectiveTtl is null ? null : now + effectiveTtl, purpose)),
            ct);
    }

    public async Task<byte[]?> GetAsync(
        string tenantId, string purpose, string objectId, CancellationToken ct = default)
    {
        var path = ResolvePath(tenantId, purpose, objectId);
        if (!File.Exists(path))
            return null;

        // Expired-but-not-yet-swept objects are already unreadable (fail closed).
        var meta = await ReadMetaAsync(MetaPath(path), ct);
        if (meta?.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
            return null;

        var blob = await File.ReadAllBytesAsync(path, ct);
        var plaintext = new byte[blob.Length - NonceSize - TagSize];
        using var aes = new AesGcm(ResolveKey(), TagSize);
        aes.Decrypt(
            blob.AsSpan(0, NonceSize),
            blob.AsSpan(NonceSize + TagSize),
            blob.AsSpan(NonceSize, TagSize),
            plaintext);
        return plaintext;
    }

    public Task<bool> DeleteAsync(
        string tenantId, string purpose, string objectId, CancellationToken ct = default)
    {
        var path = ResolvePath(tenantId, purpose, objectId);
        var existed = File.Exists(path);
        if (existed)
            File.Delete(path);
        if (File.Exists(MetaPath(path)))
            File.Delete(MetaPath(path));
        return Task.FromResult(existed);
    }

    public async Task<IReadOnlyList<PrunedObject>> SweepExpiredAsync(
        DateTimeOffset now, CancellationToken ct = default)
    {
        var pruned = new List<PrunedObject>();
        if (!Directory.Exists(Root))
            return pruned;

        foreach (var metaPath in Directory.EnumerateFiles(Root, "*.meta.json", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var meta = await ReadMetaAsync(metaPath, ct);
            if (meta?.ExpiresAt is not { } expiresAt || expiresAt > now)
                continue;

            // {root}/{tenant}/{purpose}/{id}.meta.json
            var purposeDir = Path.GetDirectoryName(metaPath)!;
            var tenantDir = Path.GetDirectoryName(purposeDir)!;
            var objectId = Path.GetFileName(metaPath)[..^".meta.json".Length];

            var binPath = Path.Combine(purposeDir, objectId + ".bin");
            if (File.Exists(binPath))
                File.Delete(binPath);
            File.Delete(metaPath);

            pruned.Add(new PrunedObject(
                Path.GetFileName(tenantDir), Path.GetFileName(purposeDir), objectId, expiresAt));
        }

        return pruned;
    }

    private byte[] ResolveKey()
    {
        // Lazy on purpose: a deployment without the secret boots fine and fails on first USE
        // with SecretNotFoundException (never logged; ISecretStore custody rules apply).
        var encoded = secrets.GetRequiredSecret(EncryptionKeySecretName);
        byte[] key;
        try
        {
            key = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                $"Secret '{EncryptionKeySecretName}' must be base64. The value is not included in this error.");
        }
        return key.Length == 32
            ? key
            : throw new InvalidOperationException(
                $"Secret '{EncryptionKeySecretName}' must decode to 32 bytes (AES-256), got {key.Length}.");
    }

    private string ResolvePath(string tenantId, string purpose, string objectId)
    {
        ValidateSegment(tenantId, nameof(tenantId));
        ValidateSegment(purpose, nameof(purpose));
        ValidateSegment(objectId, nameof(objectId));
        return Path.Combine(Root, tenantId, purpose, objectId + ".bin");
    }

    private static void ValidateSegment(string value, string name)
    {
        if (!SafeSegment().IsMatch(value))
            throw new ArgumentException(
                $"'{name}' contains characters not allowed in an object-store path segment.", name);
    }

    private static string MetaPath(string binPath)
        => binPath[..^".bin".Length] + ".meta.json";

    private static async Task<ObjectMeta?> ReadMetaAsync(string metaPath, CancellationToken ct)
    {
        if (!File.Exists(metaPath))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ObjectMeta>(await File.ReadAllTextAsync(metaPath, ct));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
