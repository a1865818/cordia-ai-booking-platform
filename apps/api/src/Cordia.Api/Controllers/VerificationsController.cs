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
public class VerificationsController(IMediator mediator, ITenantContext tenant) : ControllerBase
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
        return Ok(new RequestVerificationResponse(result.Status, result.Verification));
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
        [FromHeader(Name = "X-Correlation-Id")] string? correlationId,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var result = await mediator.Send(
            new VerifyCodeCommand(
                tenant.TenantId,
                verificationId,
                request.Code,
                string.IsNullOrWhiteSpace(correlationId) ? HttpContext.TraceIdentifier : correlationId),
            ct);
        return Ok(new CheckVerificationResponse(result.Status, result.Grant));
    }
}

public sealed record RequestVerificationResponse(
    [property: JsonPropertyName("status")] RequestVerificationStatusEnum Status,
    [property: JsonPropertyName("verification")] VerificationSentResult? Verification);

public sealed record CheckVerificationResponse(
    [property: JsonPropertyName("status")] CheckVerificationStatusEnum Status,
    [property: JsonPropertyName("grant")] VerificationGrantView? Grant);

public sealed record VerifyCodeRequest([property: JsonPropertyName("code")] string Code);
