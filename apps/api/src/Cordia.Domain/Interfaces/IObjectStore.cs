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
    /// <summary>Stores an object, encrypting at rest. <paramref name="ttl"/> bounds its lifetime;
    /// when null, purpose-specific defaults may apply (raw-audio → 72h). Contract: each object id
    /// is written ONCE, or re-PUT idempotently on a retry with equivalent content — a substantive
    /// overwrite of a live id is not supported (a crash mid-write may briefly serve prior content).</summary>
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

    /// <summary>Lists the object ids under (tenant, purpose) whose id starts with
    /// <paramref name="prefix"/> (empty ⇒ all), ordinal-sorted — e.g. a session's transcript
    /// turns <c>{sessionId}.000001…</c> in order. Ids only; content stays encrypted at rest.</summary>
    Task<IReadOnlyList<string>> ListIdsAsync(
        string tenantId, string purpose, string prefix = "", CancellationToken ct = default);

    /// <summary>IDENTIFIES (without deleting) every object whose TTL elapsed before
    /// <paramref name="now"/> — or whose meta is missing/corrupt — so the caller can durably
    /// record the removal (<c>privacy.retention.object_pruned</c>) BEFORE deleting via
    /// <see cref="DeleteAsync"/>. Idempotent: an object left undeleted (crash, or a failed delete)
    /// is simply re-identified next cycle. Content-less orphan meta sidecars are reaped in passing
    /// (no audit obligation) and not returned.</summary>
    Task<IReadOnlyList<PrunedObject>> ScanExpiredAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Convenience scan-then-delete (see <see cref="ScanExpiredAsync"/>): deletes every
    /// expired object and returns what was pruned. Does NOT give the audit-before-delete ordering
    /// retention requires — for that, use <see cref="ScanExpiredAsync"/> + <see cref="DeleteAsync"/>.</summary>
    Task<IReadOnlyList<PrunedObject>> SweepExpiredAsync(DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>One object removed by <see cref="IObjectStore.SweepExpiredAsync"/>.
/// <paramref name="ExpiresAt"/> is null when the object was pruned fail-closed for a
/// missing/unparseable meta sidecar rather than an elapsed TTL; <paramref name="Reason"/>
/// distinguishes the two (<c>ttl_elapsed</c> vs <c>missing_or_corrupt_meta</c>).</summary>
public sealed record PrunedObject(
    string TenantId,
    string Purpose,
    string ObjectId,
    DateTimeOffset? ExpiresAt,
    string Reason);
