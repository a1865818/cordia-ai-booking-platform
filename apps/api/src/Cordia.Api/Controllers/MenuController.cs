using System.Text.Json.Serialization;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.Menu.ApproveCatalog;
using Cordia.Application.Handlers.Menu.EditCatalogItems;
using Cordia.Application.Handlers.Menu.GetCatalogDraftItems;
using Cordia.Application.Handlers.Menu.ListCatalogs;
using Cordia.Application.Handlers.Menu.PublishCatalog;
using Cordia.Application.Handlers.Menu.ReviewCatalog;
using Cordia.Application.Handlers.Menu.UploadMenu;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using Cordia.Domain.Menu;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Owner-facing endpoints for the vendor-catalog lifecycle (spec 11 §5.2).
///
/// POST /api/menu/catalogs                       → 202 (upload + enqueue ingestion)
/// POST /api/menu/catalogs/{catalogId}/review    → 200 | 404 | 409
/// POST /api/menu/catalogs/{catalogId}/approve   → 200 | 404 | 409
/// POST /api/menu/catalogs/{catalogId}/publish   → 200 | 404 | 409
///
/// Thin by design: the controller resolves tenant (X-Tenant-Id) + actor (X-Actor) + reason code
/// (X-Reason-Code), sends a MediatR command, and maps typed domain exceptions to status codes via
/// the GlobalExceptionHandler (CatalogNotFound → 404, InvalidCatalogStateTransition → 409).
/// Spec 14 §24.5 fail-closed (gate decision 2026-07-10): X-Actor and X-Reason-Code are MANDATORY
/// for these owner actions — missing values are rejected by the validators/operator behavior, and
/// every action is audited with actor + reason code. Role checks remain the tracked 08/14 open
/// decision.
/// </summary>
[ApiController]
[Route("api/menu")]
public class MenuController(IMediator mediator, ITenantContext tenant, IActorContext actorContext) : ControllerBase
{
    /// <summary>Uploads a menu artifact and enqueues ingestion. Returns 202 with the new catalog id.</summary>
    [HttpPost("catalogs")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Upload(
        [FromBody] UploadMenuRequest body,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        // source_artifact_ref is enforced by UploadMenuValidator via the validation pipeline —
        // validators are the sole source of required-field enforcement (docs/dotnet-api-patterns.md).

        var versionLabel = string.IsNullOrWhiteSpace(body.VersionLabel)
            ? $"menu_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}"
            : body.VersionLabel;
        var (resolvedActor, resolvedRole) = ActorBinder.Resolve(actorContext, actor, actorRole);

        var catalogId = await mediator.Send(
            new UploadMenuCommand(tenant.TenantId, resolvedActor, body.SourceArtifactRef, versionLabel, reasonCode ?? string.Empty, resolvedRole), ct);

        return Accepted(
            $"/api/menu/catalogs/{catalogId}",
            new { catalog_id = catalogId, status = "uploaded" });
    }

    /// <summary>Opens a draft catalog for review (draft_ready → in_review), optionally editing items.</summary>
    [HttpPost("catalogs/{catalogId}/review")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Review(
        string catalogId,
        [FromBody] ReviewCatalogRequest? body,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var edits = (body?.Edits ?? [])
            .Where(e => e is not null) // a null array element (e.g. "edits":[null]) must not 500
            .Select(e => new CatalogItemEdit(e.ItemId, e.Name, e.Description, e.PriceMinor))
            .ToList();
        var (resolvedActor, resolvedRole) = ActorBinder.Resolve(actorContext, actor, actorRole);

        await mediator.Send(
            new ReviewCatalogCommand(tenant.TenantId, catalogId, resolvedActor, edits, reasonCode ?? string.Empty, resolvedRole), ct);

        return Ok(new { catalog_id = catalogId, status = "in_review" });
    }

    /// <summary>
    /// Owner discovery read: the tenant's catalogs at any status, newest first.
    ///
    /// Closes docs §12 gap 3 — without it the console could only ever show catalogs started in
    /// its own browser session, so an ingestion begun elsewhere was invisible. Each row carries
    /// `age_seconds` — time at its CURRENT status, which is the same clock `MenuIngestionMonitor`
    /// sweeps on, so the console's "looks stuck" warning and the server's `extract_failed` verdict
    /// can never disagree about the same catalog. Age is the pre-verdict signal; the status is the
    /// verdict.
    /// </summary>
    [HttpGet("catalogs")]
    [ProducesResponseType(typeof(CatalogListResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CatalogListResult>> ListCatalogs(
        [FromQuery(Name = "limit")] int? limit,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var (resolvedActor, resolvedRole) = ActorBinder.Resolve(actorContext, actor, actorRole);
        var result = await mediator.Send(
            new ListCatalogsQuery(
                tenant.TenantId, resolvedActor,
                reasonCode ?? string.Empty, resolvedRole, limit ?? 50),
            ct);

        return Ok(result);
    }

    /// <summary>
    /// Owner read of a catalog's items at any status, with the catalog's own status.
    ///
    /// The draft-scoped counterpart to the published-only catalog reads: it returns the
    /// namespaced <c>{catalogId}:{rawId}</c> ids that <c>POST .../edit</c> requires and which no
    /// other endpoint can surface while a catalog is <c>in_review</c> (docs §12 gap 4). The
    /// status it carries is what lets the console observe real lifecycle progress rather than
    /// only the statuses its own writes confirmed (gap 3).
    ///
    /// Typed <see cref="ActionResult{T}"/> so the generated TS contract carries a body rather
    /// than <c>content?: never</c>.
    /// </summary>
    [HttpGet("catalogs/{catalogId}/items")]
    [ProducesResponseType(typeof(CatalogDraftItemsResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CatalogDraftItemsResult>> GetDraftItems(
        string catalogId,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var (resolvedActor, resolvedRole) = ActorBinder.Resolve(actorContext, actor, actorRole);
        var result = await mediator.Send(
            new GetCatalogDraftItemsQuery(
                tenant.TenantId, catalogId, resolvedActor,
                reasonCode ?? string.Empty, resolvedRole),
            ct);

        return Ok(result);
    }

    /// <summary>Edits draft items while the catalog is in_review (no status change; repeatable).</summary>
    [HttpPost("catalogs/{catalogId}/edit")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> EditItems(
        string catalogId,
        [FromBody] EditCatalogItemsRequest body,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var edits = (body.Edits ?? [])
            .Where(e => e is not null) // a null array element (e.g. "edits":[null]) must not 500
            .Select(e => new CatalogItemEdit(e.ItemId, e.Name, e.Description, e.PriceMinor))
            .ToList();
        var (resolvedActor, resolvedRole) = ActorBinder.Resolve(actorContext, actor, actorRole);

        await mediator.Send(
            new EditCatalogItemsCommand(tenant.TenantId, catalogId, resolvedActor, edits, reasonCode ?? string.Empty, resolvedRole), ct);

        return Ok(new { catalog_id = catalogId, status = "in_review", edited = edits.Count });
    }

    /// <summary>Approves a reviewed catalog (in_review → approved).</summary>
    [HttpPost("catalogs/{catalogId}/approve")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Approve(
        string catalogId,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        var (resolvedActor, resolvedRole) = ActorBinder.Resolve(actorContext, actor, actorRole);

        await mediator.Send(new ApproveCatalogCommand(tenant.TenantId, catalogId, resolvedActor, reasonCode ?? string.Empty, resolvedRole), ct);

        return Ok(new { catalog_id = catalogId, status = "approved" });
    }

    /// <summary>Publishes an approved catalog (approved → published) and supersedes the prior version.</summary>
    [HttpPost("catalogs/{catalogId}/publish")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Publish(
        string catalogId,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        var (resolvedActor, resolvedRole) = ActorBinder.Resolve(actorContext, actor, actorRole);

        await mediator.Send(new PublishCatalogCommand(tenant.TenantId, catalogId, resolvedActor, reasonCode ?? string.Empty, resolvedRole), ct);

        return Ok(new { catalog_id = catalogId, status = "published" });
    }
}

/// <summary>Body for POST /api/menu/catalogs.</summary>
public sealed record UploadMenuRequest(
    [property: JsonPropertyName("source_artifact_ref")] string SourceArtifactRef,
    [property: JsonPropertyName("version_label")] string? VersionLabel)
{
    // Parameterless ctor for model binding when optional fields are absent.
    public UploadMenuRequest() : this(string.Empty, null) { }
}

/// <summary>Body for POST /api/menu/catalogs/{catalogId}/review.</summary>
public sealed record ReviewCatalogRequest(
    [property: JsonPropertyName("edits")] IReadOnlyList<CatalogItemEditDto>? Edits)
{
    public ReviewCatalogRequest() : this((IReadOnlyList<CatalogItemEditDto>?)null) { }
}

/// <summary>Body for POST /api/menu/catalogs/{catalogId}/edit.</summary>
public sealed record EditCatalogItemsRequest(
    [property: JsonPropertyName("edits")] IReadOnlyList<CatalogItemEditDto>? Edits)
{
    public EditCatalogItemsRequest() : this((IReadOnlyList<CatalogItemEditDto>?)null) { }
}

/// <summary>
/// A single owner item edit in a review request (null fields left unchanged).
/// <para><b><c>item_id</c> must be the full namespaced id the read endpoints emit</b>
/// (<c>{catalogId}:{rawId}</c> — as returned by <c>GET /api/catalog/items</c> and
/// <c>/api/menu/...</c>), NOT the raw menu id. An edit whose <c>item_id</c> matches no item in the
/// editable catalog is rejected with 404 (never silently ignored).</para>
/// </summary>
public sealed record CatalogItemEditDto(
    [property: JsonPropertyName("item_id")] string ItemId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("price_minor")] long? PriceMinor)
{
    public CatalogItemEditDto() : this(string.Empty, null, null, null) { }
}
