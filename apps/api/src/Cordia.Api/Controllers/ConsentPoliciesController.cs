using Cordia.Application.Handlers.Security.ResolveJurisdictionPolicy;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Recording-consent jurisdiction policy resolution (spec 14 §5.1). The wire
/// shape is the generated cross-service ConsentPolicyView so agent↔API drift
/// is impossible. The view also carries the tenant redaction policy (§4.1):
/// the agent's consent gate installs it via set_redaction_policy, making
/// ConsentPolicyView the runtime transport for tenant-configured sensitive
/// fields (Redaction:SensitiveFields config; empty config ⇒ null ⇒ the agent's
/// always-on baseline stays active).
/// </summary>
[ApiController]
[Route("api/consent-policies")]
public class ConsentPoliciesController(
    IMediator mediator,
    ITenantContext tenant,
    IConfiguration config) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(ConsentPolicyView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ConsentPolicyView>> GetConsentPolicy(
        [FromQuery] string channel,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var policy = await mediator.Send(
            new ResolveJurisdictionPolicyQuery(tenant.TenantId, channel), ct);

        return Ok(new ConsentPolicyView
        {
            JurisdictionPolicyId = policy.JurisdictionPolicyId,
            ConsentPolicyVersion = policy.ConsentPolicyVersion,
            NoticeRequired = policy.NoticeRequired,
            // Null is a REAL wire value here (spec: None ⇒ agent plays its default
            // notice; the agent does `policy.notice_text or DEFAULT_NOTICE`). The
            // generated property is non-nullable only because of the tracked
            // quicktype nullability gap (see Program.cs) — do NOT "fix" this to
            // `?? string.Empty`; that would change wire semantics.
            NoticeText = policy.NoticeText!,
            // Null (omitted) when no tenant fields are configured — additive field,
            // older agents and the baseline-only posture are unaffected.
            Redaction = BuildRedactionPolicy()!,
        });
    }

    /// <summary>
    /// Builds the tenant redaction policy from <c>Redaction:SensitiveFields</c>
    /// (gate decision 2026-07-09: PAN/CVV/gov-id always-on; tenant list empty by
    /// default). Returns null when nothing is configured so the wire field is
    /// omitted and the agent keeps its baseline.
    /// </summary>
    private RedactionPolicy? BuildRedactionPolicy()
    {
        var sensitiveFields = config.GetSection("Redaction:SensitiveFields").Get<string[]?>();
        if (sensitiveFields is null || sensitiveFields.Length == 0)
            return null;

        return new RedactionPolicy
        {
            MaskPan = true,
            MaskCvv = true,
            MaskGovernmentId = true,
            ConfiguredSensitiveFields = [.. sensitiveFields],
            MaskToken = "[REDACTED]",
        };
    }
}
