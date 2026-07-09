using Cordia.Domain.Enums;
using Cordia.Domain.Security;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Resolves the recording-consent jurisdiction policy for a (tenant, channel)
/// pair (spec 14 §5.1). Implementations MUST fail closed: when no policy is
/// configured, the result has NoticeRequired = true.
/// </summary>
public interface IJurisdictionPolicyProvider
{
    JurisdictionPolicy Resolve(string tenantId, TenantChannel channel);
}
