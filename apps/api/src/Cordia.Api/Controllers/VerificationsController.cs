using System.Text.Json.Serialization;
using Cordia.Application.Handlers.Verification.CheckVerification;
using Cordia.Application.Handlers.Verification.RequestVerification;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

[ApiController]
[Route("api/verifications")]
public class VerificationsController(
    IMediator mediator,
    ITenantContext tenant,
    ICorrelationContext correlation) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(RequestVerificationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RequestVerificationResponse>> RequestVerification(
        [FromBody] VerificationRequest request,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var result = await mediator.Send(new RequestVerificationCommand(tenant.TenantId, request), ct);
        return Ok(new RequestVerificationResponse(result.Status, result.Verification, result.DevelopmentCode));
    }

    [HttpGet("{verificationId}")]
    [ProducesResponseType(typeof(CheckVerificationResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CheckVerificationResponse>> GetVerification(
        string verificationId,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var result = await mediator.Send(new CheckVerificationQuery(tenant.TenantId, verificationId), ct);
        return Ok(new CheckVerificationResponse(result.Status, result.Grant));
    }

    [HttpPost("{verificationId}/verify")]
    [ProducesResponseType(typeof(CheckVerificationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CheckVerificationResponse>> VerifyCode(
        string verificationId,
        [FromBody] VerifyCodeRequest request,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        // #77: this used to bind [FromHeader("X-Correlation-Id")] directly, which took the raw
        // client value straight past CorrelationMiddleware's SafeIdRegex (^[A-Za-z0-9_-]{1,64}$)
        // — the guard that exists precisely so an attacker-chosen id cannot be written into audit
        // and logs. It then fell back to HttpContext.TraceIdentifier rather than the minted id, so
        // an unverified correlation could enter the §25.1 chain from a second, unsanctioned entry
        // point. Spec 15 §285 makes the gateway the correlation originator, with the middleware as
        // the sanctioned mint point for direct API calls; there is no third.
        //
        // Reading the ambient context means the value is always either a validated echo or a
        // server-minted corr_ id, and the fallback disappears with the second path.
        var result = await mediator.Send(
            new VerifyCodeCommand(
                tenant.TenantId,
                verificationId,
                request.Code,
                correlation.Current.CorrelationId),
            ct);
        return Ok(new CheckVerificationResponse(result.Status, result.Grant));
    }
}

/// <param name="DevelopmentCode">
/// The code just issued — present ONLY where <c>Verification:ExposeCodeForDevelopment</c> is on
/// (Development and the test host), because no delivery channel exists yet (#71). Omitted from the
/// body otherwise.
/// </param>
public sealed record RequestVerificationResponse(
    [property: JsonPropertyName("status")] RequestVerificationStatusEnum Status,
    [property: JsonPropertyName("verification")] VerificationSentResult? Verification,
    [property: JsonPropertyName("development_code"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? DevelopmentCode = null);

public sealed record CheckVerificationResponse(
    [property: JsonPropertyName("status")] CheckVerificationStatusEnum Status,
    [property: JsonPropertyName("grant")] VerificationGrantView? Grant);

public sealed record VerifyCodeRequest([property: JsonPropertyName("code")] string Code);
