using Cordia.Application.Handlers.Security.RecordConsent;
using Cordia.Application.Handlers.Security.ResolveJurisdictionPolicy;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Recording-consent endpoints (spec 14 §5.1, D12). The wire shapes are the
/// generated cross-service contracts (ConsentPolicyView / SessionConsentState /
/// ConsentRecordedResult) so the agent and API cannot drift.
/// </summary>
[ApiController]
public class ConsentController(IMediator mediator, ITenantContext tenant) : ControllerBase
{
    private static readonly IReadOnlyDictionary<Outcome, string> OutcomeWire =
        new Dictionary<Outcome, string>
        {
            [Outcome.Granted] = "granted",
            [Outcome.Declined] = "declined",
            [Outcome.NotRequired] = "not_required",
            [Outcome.AlternativeOffered] = "alternative_offered",
        };

    private static readonly IReadOnlyDictionary<NoticeChannel, string> NoticeChannelWire =
        new Dictionary<NoticeChannel, string>
        {
            [NoticeChannel.Voice] = "voice",
            [NoticeChannel.Web] = "web",
            [NoticeChannel.Messaging] = "messaging",
            [NoticeChannel.Email] = "email",
        };

    [HttpGet("api/consent-policies")]
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
            NoticeText = policy.NoticeText,
        });
    }

    [HttpPost("api/conversation-sessions/{sessionId}/consent")]
    [ProducesResponseType(typeof(ConsentRecordedResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConsentRecordedResult>> RecordConsent(
        string sessionId,
        [FromBody] SessionConsentState consent,
        [FromHeader(Name = "X-Correlation-Id")] string? correlationId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            return Problem(statusCode: 400, title: "Missing tenant", detail: "X-Tenant-Id header is required.");

        var result = await mediator.Send(
            new RecordConsentCommand(
                tenant.TenantId,
                sessionId,
                OutcomeWire[consent.Outcome],
                consent.ConsentPolicyVersion,
                consent.JurisdictionPolicyId,
                consent.RecordingAllowed,
                consent.CapturedAt,
                NoticeChannelWire[consent.NoticeChannel],
                string.IsNullOrWhiteSpace(correlationId) ? HttpContext.TraceIdentifier : correlationId),
            ct);

        return Ok(new ConsentRecordedResult
        {
            SessionId = result.SessionId,
            RecordingAllowed = result.RecordingAllowed,
        });
    }
}
