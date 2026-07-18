namespace Cordia.Domain.Security;

/// <summary>
/// Resolved recording-consent policy for a (tenant, channel) pair (spec 14 §5.1).
/// Resolution is fail-closed: when no configuration exists for the pair, the
/// provider returns a policy with <see cref="NoticeRequired"/> = true.
/// </summary>
public sealed record JurisdictionPolicy(
    string JurisdictionPolicyId,
    string ConsentPolicyVersion,
    bool NoticeRequired,
    string? NoticeText);
