using Cordia.Application.Handlers.Security.ResolveJurisdictionPolicy;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Recording-consent jurisdiction policy resolution (spec 14 §5.1). The wire
/// shape is the generated cross-service ConsentPolicyView so agent↔API drift
/// is impossible.
/// </summary>
[ApiController]
[Route("api/consent-policies")]
public class ConsentPoliciesController(IMediator mediator, ITenantContext tenant) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(ConsentPolicyView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ConsentPolicyView>> GetConsentPolicy(
        [FromQuery] string channel,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            return Problem(statusCode: 400, title: "Missing tenant", detail: "X-Tenant-Id header is required.");

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
        });
    }
}
