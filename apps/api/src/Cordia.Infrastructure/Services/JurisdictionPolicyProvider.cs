using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using Microsoft.Extensions.Configuration;

namespace Cordia.Infrastructure.Services;

/// <summary>
/// Configuration-backed jurisdiction-policy resolution (spec 14 §5.1).
///
/// Reads the <c>Consent</c> configuration section:
/// <code>
/// "Consent": {
///   "DefaultJurisdictionPolicyId": "jp_default",
///   "DefaultConsentPolicyVersion": "default:2026-01",
///   "Channels": {
///     "Web": { "NoticeRequired": false, "JurisdictionPolicyId": "jp_web", ... }
///   }
/// }
/// </code>
/// Fail-closed (spec 14 §6): when nothing is configured for the channel, the
/// notice is REQUIRED. The <paramref name="tenantId"/> parameter is reserved
/// for per-tenant policy overrides (open decision: jurisdiction mapping);
/// today all tenants share the deployment-level configuration.
/// </summary>
public sealed class JurisdictionPolicyProvider(IConfiguration configuration) : IJurisdictionPolicyProvider
{
    public JurisdictionPolicy Resolve(string tenantId, TenantChannel channel)
    {
        var root = configuration.GetSection("Consent");
        var section = root.GetSection($"Channels:{channel}");

        var noticeRequired = section.GetValue<bool?>("NoticeRequired") ?? true;
        var policyId = section.GetValue<string?>("JurisdictionPolicyId")
            ?? root.GetValue<string?>("DefaultJurisdictionPolicyId")
            ?? "jp_default";
        var policyVersion = section.GetValue<string?>("ConsentPolicyVersion")
            ?? root.GetValue<string?>("DefaultConsentPolicyVersion")
            ?? "default:2026-01";
        var noticeText = section.GetValue<string?>("NoticeText");

        return new JurisdictionPolicy(policyId, policyVersion, noticeRequired, noticeText);
    }
}
