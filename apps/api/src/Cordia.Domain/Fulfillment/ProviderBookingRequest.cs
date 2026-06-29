namespace Cordia.Domain.Fulfillment;

/// <summary>Request envelope passed to a booking provider adapter.</summary>
/// <param name="TenantId">Tenant that owns the operation.</param>
/// <param name="OperationId">Operation being fulfilled.</param>
/// <param name="ProviderAccount">Provider account identifier (e.g. merchant ID or location ID).</param>
/// <param name="ProviderIdempotencyKey">
///   Stable key bound at submission (Inv-2/3). MUST NOT change across retries — the provider uses it
///   to detect and suppress duplicate creates.
/// </param>
/// <param name="RequestHash">Canonical SHA-256 hash of the payload (spec 04 §5).</param>
/// <param name="CanonicalPayloadJson">Fully canonicalized JSON payload; what is hashed by <paramref name="RequestHash"/>.</param>
public sealed record ProviderBookingRequest(
    string TenantId,
    string OperationId,
    string ProviderAccount,
    string ProviderIdempotencyKey,
    string RequestHash,
    string CanonicalPayloadJson);
