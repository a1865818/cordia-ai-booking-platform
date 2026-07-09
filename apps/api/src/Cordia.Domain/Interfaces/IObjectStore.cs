namespace Cordia.Domain.Interfaces;

/// <summary>
/// Tenant-scoped encrypted blob custody (spec 14 §24.3/§24.4): call transcripts, raw audio,
/// and similar sensitive artifacts live behind this seam — encrypted at rest, path-isolated
/// per tenant, and swept by the retention service. Objects are addressed by
/// (tenantId, purpose, objectId); <c>purpose</c> is a short tag such as
/// <c>"raw-audio"</c> or <c>"transcript"</c> that groups artifacts with a shared retention
/// posture. Raw audio receives a short default TTL (gate decision 2026-07-10: 72h) even when
/// the caller passes none — fail closed on retention.
/// </summary>
public interface IObjectStore
{
    /// <summary>Stores (or overwrites) an object, encrypting at rest. <paramref name="ttl"/>
    /// bounds its lifetime; when null, purpose-specific defaults may apply (raw-audio → 72h).</summary>
    Task PutAsync(
        string tenantId,
        string purpose,
        string objectId,
        ReadOnlyMemory<byte> content,
        TimeSpan? ttl = null,
        CancellationToken ct = default);

    /// <summary>Returns the decrypted content, or null when absent or already expired.</summary>
    Task<byte[]?> GetAsync(string tenantId, string purpose, string objectId, CancellationToken ct = default);

    /// <summary>Deletes the object; true when something was removed.</summary>
    Task<bool> DeleteAsync(string tenantId, string purpose, string objectId, CancellationToken ct = default);

    /// <summary>Deletes every object whose TTL elapsed before <paramref name="now"/> and
    /// returns what was pruned so the caller can audit each removal
    /// (<c>privacy.retention.object_pruned</c>).</summary>
    Task<IReadOnlyList<PrunedObject>> SweepExpiredAsync(DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>One object removed by <see cref="IObjectStore.SweepExpiredAsync"/>.</summary>
public sealed record PrunedObject(
    string TenantId,
    string Purpose,
    string ObjectId,
    DateTimeOffset ExpiresAt);
