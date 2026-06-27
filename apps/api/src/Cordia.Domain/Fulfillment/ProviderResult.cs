namespace Cordia.Domain.Fulfillment;

/// <summary>Normalized result of a single call to a booking provider adapter.</summary>
/// <param name="Kind">The outcome kind.</param>
/// <param name="ProviderReference">External booking reference returned by the provider on success or lookup.</param>
/// <param name="ErrorCode">Provider-supplied error code, if any.</param>
/// <param name="SanitizedResponseMeta">
///   Jsonb-ready metadata string, safe to persist. MUST NOT contain secrets, PAN, CVV, or credentials (Inv-12).
/// </param>
public sealed record ProviderResult(
    ProviderResultKind Kind,
    string? ProviderReference = null,
    string? ErrorCode = null,
    string? SanitizedResponseMeta = null);
