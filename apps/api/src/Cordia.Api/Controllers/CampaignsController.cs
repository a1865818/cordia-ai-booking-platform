using System.Text.Json.Serialization;
using Cordia.Application.Handlers.Campaigns;
using Cordia.Application.Handlers.Campaigns.ApproveCampaign;
using Cordia.Application.Handlers.Campaigns.CreateCampaign;
using Cordia.Application.Handlers.Campaigns.GetAudienceEstimate;
using Cordia.Application.Handlers.Campaigns.ListCampaigns;
using Cordia.Application.Handlers.Campaigns.RejectCampaign;
using Cordia.Application.Handlers.Campaigns.ScheduleCampaign;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Outbound campaigns (G3). Awaiting is first-class; send is not implemented.
///
/// GET  /api/campaigns                         → 200
/// POST /api/campaigns                         → 201 (owner)
/// POST /api/campaigns/{id}/approve            → 200 | 404 | 409 (owner + reason)
/// POST /api/campaigns/{id}/reject             → 200 | 404 | 409 (owner + reason)
/// POST /api/campaigns/{id}/schedule           → 200 | 404 | 409 (owner; 409 if unapproved)
/// GET  /api/campaigns/{id}/audience-estimate  → 200 | 404
/// </summary>
[ApiController]
[Route("api/campaigns")]
public class CampaignsController(IMediator mediator, ITenantContext tenant) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(CampaignListResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CampaignListResult>> List(CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        return Ok(await mediator.Send(new ListCampaignsQuery(tenant.TenantId), ct));
    }

    [HttpPost]
    [ProducesResponseType(typeof(CampaignDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CampaignDto>> Create(
        [FromBody] CreateCampaignRequest? body,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var created = await mediator.Send(
            new CreateCampaignCommand(
                tenant.TenantId,
                body?.Title ?? string.Empty,
                body?.Audience ?? string.Empty,
                body?.Channel ?? string.Empty,
                body?.Message ?? string.Empty,
                body?.Recipients,
                actor ?? string.Empty,
                reasonCode ?? string.Empty,
                actorRole ?? string.Empty),
            ct);

        return Created($"/api/campaigns/{created.Id}", created);
    }

    [HttpPost("{id}/approve")]
    [ProducesResponseType(typeof(CampaignDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CampaignDto>> Approve(
        string id,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        return Ok(await mediator.Send(
            new ApproveCampaignCommand(
                tenant.TenantId, id, actor ?? string.Empty,
                reasonCode ?? string.Empty, actorRole ?? string.Empty),
            ct));
    }

    [HttpPost("{id}/reject")]
    [ProducesResponseType(typeof(CampaignDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CampaignDto>> Reject(
        string id,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        return Ok(await mediator.Send(
            new RejectCampaignCommand(
                tenant.TenantId, id, actor ?? string.Empty,
                reasonCode ?? string.Empty, actorRole ?? string.Empty),
            ct));
    }

    [HttpPost("{id}/schedule")]
    [ProducesResponseType(typeof(CampaignDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CampaignDto>> Schedule(
        string id,
        [FromBody] ScheduleCampaignRequest? body,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        return Ok(await mediator.Send(
            new ScheduleCampaignCommand(
                tenant.TenantId, id, body?.SendAt, actor ?? string.Empty,
                reasonCode ?? string.Empty, actorRole ?? string.Empty),
            ct));
    }

    [HttpGet("{id}/audience-estimate")]
    [ProducesResponseType(typeof(AudienceEstimateResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AudienceEstimateResult>> AudienceEstimate(
        string id, CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        return Ok(await mediator.Send(new GetAudienceEstimateQuery(tenant.TenantId, id), ct));
    }
}

/// <summary>Body for POST /api/campaigns.</summary>
public sealed record CreateCampaignRequest(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("audience")] string? Audience,
    [property: JsonPropertyName("channel")] string? Channel,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("recipients")] int? Recipients)
{
    public CreateCampaignRequest() : this(null, null, null, null, null) { }
}

/// <summary>Body for POST /api/campaigns/{id}/schedule.</summary>
public sealed record ScheduleCampaignRequest(
    [property: JsonPropertyName("send_at")] DateTimeOffset? SendAt)
{
    public ScheduleCampaignRequest() : this((DateTimeOffset?)null) { }
}
