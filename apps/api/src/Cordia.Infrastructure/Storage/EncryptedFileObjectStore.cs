using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
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
    private const string TranscriptPurpose = "transcript";
    private const string ModelTracePurpose = "model-trace";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    // Prune reason codes forwarded verbatim into the privacy.retention.object_pruned audit
    // (RetentionSweeper) — the single source so producer and audit can never drift on the string.
    private const string ReasonTtlElapsed = "ttl_elapsed";
    private const string ReasonMissingOrCorruptMeta = "missing_or_corrupt_meta";

    // A meta sidecar with no blob is only reaped once it is older than this — long enough that a
    // concurrent in-flight PutAsync (meta renamed, blob not yet; a millisecond window) is safe.
    private static readonly TimeSpan OrphanMetaGrace = TimeSpan.FromHours(1);

    // Conservative allow-list for tenant/purpose/id path segments — the single source is
    // Domain's ObjectStoreSegment so API validators (400) and this fail-closed guard agree.
    private static Regex SafeSegment() => ObjectStoreSegment.Pattern();

    private sealed record ObjectMeta(
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
        [property: JsonPropertyName("purpose")] string Purpose);

    private string Root => config["ObjectStore:Root"]
        ?? Path.Combine(Directory.GetCurrentDirectory(), ".objectstore");

    private double RawAudioTtlHours => config.GetValue("ObjectStore:RawAudioTtlHours", 72d);

    private double TranscriptTtlDays => config.GetValue("ObjectStore:TranscriptTtlDays", 90d);

    private double ModelTraceTtlDays => config.GetValue("ObjectStore:ModelTraceTtlDays", 14d);

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
        // Fail closed on retention: sensitive purposes never persist without a TTL when the
        // caller passes none (gate decisions: raw audio 72h; GD-3 2026-07-11: transcripts 90d,
        // model traces 14d — strictly shorter, spec 13 §25.3).
        var effectiveTtl = ttl ?? purpose switch
        {
            RawAudioPurpose => TimeSpan.FromHours(RawAudioTtlHours),
            TranscriptPurpose => TimeSpan.FromDays(TranscriptTtlDays),
            ModelTracePurpose => TimeSpan.FromDays(ModelTraceTtlDays),
            _ => (TimeSpan?)null,
        };

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

        var metaBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new ObjectMeta(now, effectiveTtl is null ? null : now + effectiveTtl, purpose)));

        // Write-once (or idempotent-overwrite) contract (L3): each object id is written once, or
        // re-PUT on a retry with EQUIVALENT content (e.g. a transcript turn {sessionId}.{seq}).
        // Meta is written FIRST, then the blob — each via a temp file + atomic rename. Meta-first
        // is chosen deliberately over blob-first: with reads/sweep fail-closed on a missing meta
        // (M3), a blob-first ordering would let SweepExpiredAsync's pass 1 see an in-flight blob
        // with no sidecar and prune a just-written NEW object mid-PUT — meta-first has no such
        // race (pass 1 enumerates .bin, and the blob appears last). The only residual crash
        // window is a SUBSTANTIVE re-PUT of an existing id (new meta + old blob → briefly serves
        // prior content); that is not a supported update pattern here (ids are write-once, and a
        // retry's content is equivalent). A crash between the two renames on a FIRST PUT leaves at
        // most a meta-only orphan, which SweepExpiredAsync's pass 2 reaps.
        await WriteAtomicAsync(MetaPath(path), metaBytes, ct);
        await WriteAtomicAsync(path, blob, ct);
    }

    /// <summary>Writes <paramref name="bytes"/> to <paramref name="finalPath"/> atomically:
    /// a sibling temp file is written then renamed into place (atomic on the same volume), so a
    /// reader never observes a partially written file and a crash leaves only a stray temp.</summary>
    private static async Task WriteAtomicAsync(string finalPath, byte[] bytes, CancellationToken ct)
    {
        var tmp = $"{finalPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, finalPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    public async Task<byte[]?> GetAsync(
        string tenantId, string purpose, string objectId, CancellationToken ct = default)
    {
        var path = ResolvePath(tenantId, purpose, objectId);
        if (!File.Exists(path))
            return null;

        // Fail closed (M3): a blob whose meta sidecar is missing or unparseable has no provable
        // retention state, so treat it as expired rather than decrypt-and-return. A present meta
        // with a null ExpiresAt is a legitimate no-TTL object and is served.
        var meta = await ReadMetaAsync(MetaPath(path), ct);
        if (meta is null)
            return null;
        if (meta.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
            return null;

        var blob = await File.ReadAllBytesAsync(path, ct);
        // A blob shorter than nonce+tag is truncated/corrupt — fail closed, never underflow the
        // plaintext length below.
        if (blob.Length < NonceSize + TagSize)
            return null;

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

    public Task<IReadOnlyList<string>> ListIdsAsync(
        string tenantId, string purpose, string prefix = "", CancellationToken ct = default)
    {
        ValidateSegment(tenantId, nameof(tenantId));
        ValidateSegment(purpose, nameof(purpose));
        // The prefix is a LEADING FRAGMENT of a valid segment, not a full segment — validate the
        // characters (traversal-proof) WITHOUT the segment length cap (L4): a caller may pass a
        // fragment longer than any storable id (e.g. a 128-char session id + '.'), which simply
        // matches nothing and returns an empty list rather than throwing → 500.
        if (prefix.Length > 0 && !ObjectStoreSegment.IsValidPrefix(prefix))
            throw new ArgumentException(
                "'prefix' contains characters not allowed in an object-store path segment.", nameof(prefix));

        var dir = Path.Combine(Root, tenantId, purpose);
        if (!Directory.Exists(dir))
            return Task.FromResult<IReadOnlyList<string>>([]);

        IReadOnlyList<string> ids = Directory.EnumerateFiles(dir, prefix + "*.bin")
            .Select(f => Path.GetFileName(f)[..^".bin".Length])
            .Where(id => id.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(ids);
    }

    public async Task<IReadOnlyList<PrunedObject>> SweepExpiredAsync(
        DateTimeOffset now, CancellationToken ct = default)
    {
        var pruned = new List<PrunedObject>();
        if (!Directory.Exists(Root))
            return pruned;

        // Pass 1 — enumerate the encrypted blobs (the content-bearing artifacts), not the meta
        // sidecars: a blob whose meta is missing/unparseable would otherwise be invisible to a
        // meta-driven sweep and linger forever (dead weight — already unreadable via GetAsync).
        // Prune it fail-closed here so the store cannot accumulate orphaned ciphertext (M3).
        //
        // Single-node assumption: read-decide-delete is not atomic against a concurrent PutAsync
        // renewing the SAME id at the instant of the sweep (a lock-free filesystem TOCTOU). The
        // current callers write each object id exactly once (transcript {sessionId}.{seq}, unique
        // trace/audio ids), so no renewal races in practice; a multi-writer deployment would need
        // per-object locking.
        foreach (var binPath in Directory.EnumerateFiles(Root, "*.bin", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var metaPath = MetaPath(binPath);
            var meta = await ReadMetaAsync(metaPath, ct);

            string reason;
            DateTimeOffset? expiresAt;
            if (meta is null)
            {
                reason = ReasonMissingOrCorruptMeta;
                expiresAt = null;
            }
            else if (meta.ExpiresAt is { } exp && exp <= now)
            {
                reason = ReasonTtlElapsed;
                expiresAt = exp;
            }
            else
            {
                // Live object: a future TTL, or a legitimate no-TTL object (ExpiresAt null).
                continue;
            }

            // {root}/{tenant}/{purpose}/{id}.bin
            var purposeDir = Path.GetDirectoryName(binPath)!;
            var tenantDir = Path.GetDirectoryName(purposeDir)!;
            var objectId = Path.GetFileName(binPath)[..^".bin".Length];

            File.Delete(binPath);
            if (File.Exists(metaPath))
                File.Delete(metaPath);

            pruned.Add(new PrunedObject(
                Path.GetFileName(tenantDir), Path.GetFileName(purposeDir), objectId, expiresAt, reason));
        }

        // Pass 2 — reap meta sidecars with no blob: crash orphans from a PutAsync that wrote the
        // meta but died before the blob rename (or from an interrupted delete above). These hold
        // only control-plane fields (no ciphertext), but would accumulate forever since pass 1
        // never sees them. Guarded by OrphanMetaGrace so an in-flight Put's not-yet-renamed blob
        // is never mistaken for an orphan; an unparseable orphan (no blob) is removed at once.
        foreach (var metaPath in Directory.EnumerateFiles(Root, "*.meta.json", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var binPath = metaPath[..^".meta.json".Length] + ".bin";
            if (File.Exists(binPath))
                continue;

            var meta = await ReadMetaAsync(metaPath, ct);
            if (meta is null || meta.CreatedAt <= now - OrphanMetaGrace)
                File.Delete(metaPath);
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
            var meta = JsonSerializer.Deserialize<ObjectMeta>(await File.ReadAllTextAsync(metaPath, ct));
            // Fail closed on a semantically-empty sidecar too (JSON "null", "{}", or a shape with
            // no purpose): a meta we never wrote is corrupt, not a legitimate no-TTL object, so it
            // must not be served or treated as live (M3).
            return string.IsNullOrEmpty(meta?.Purpose) ? null : meta;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
