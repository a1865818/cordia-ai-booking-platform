using System.Text.Json.Serialization;
using Cordia.Application.Handlers.Menu.ApproveCatalog;
using Cordia.Application.Handlers.Menu.EditCatalogItems;
using Cordia.Application.Handlers.Menu.PublishCatalog;
using Cordia.Application.Handlers.Menu.ReviewCatalog;
using Cordia.Application.Handlers.Menu.UploadMenu;
using Cordia.Domain.Interfaces;
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
/// Thin by design: the controller resolves tenant (X-Tenant-Id) + actor (X-Actor), sends a MediatR
/// command, and maps typed domain exceptions to status codes via the GlobalExceptionHandler
/// (CatalogNotFound → 404, InvalidCatalogStateTransition → 409). Authorization is a TODO; every
/// action is audited with the actor.
/// </summary>
[ApiController]
[Route("api/menu")]
public class MenuController(IMediator mediator, ITenantContext tenant) : ControllerBase
{
    /// <summary>Uploads a menu artifact and enqueues ingestion. Returns 202 with the new catalog id.</summary>
    [HttpPost("catalogs")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Upload(
        [FromBody] UploadMenuRequest body,
        [FromHeader(Name = "X-Actor")] string? actor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            return Problem(statusCode: 400, title: "Missing tenant", detail: "X-Tenant-Id header is required.");
        if (string.IsNullOrWhiteSpace(body.SourceArtifactRef))
            return Problem(statusCode: 400, title: "Missing source_artifact_ref",
                detail: "source_artifact_ref is required.");

        var versionLabel = string.IsNullOrWhiteSpace(body.VersionLabel)
            ? $"menu_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}"
            : body.VersionLabel;
        var resolvedActor = ResolveActor(actor);

        var catalogId = await mediator.Send(
            new UploadMenuCommand(tenant.TenantId, resolvedActor, body.SourceArtifactRef, versionLabel), ct);

        return Accepted(
            $"/api/menu/catalogs/{catalogId}",
            new { catalog_id = catalogId, status = "uploaded" });
    }

    /// <summary>Opens a draft catalog for review (draft_ready → in_review), optionally editing items.</summary>
    [HttpPost("catalogs/{catalogId}/review")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Review(
        string catalogId,
        [FromBody] ReviewCatalogRequest? body,
        [FromHeader(Name = "X-Actor")] string? actor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            return Problem(statusCode: 400, title: "Missing tenant", detail: "X-Tenant-Id header is required.");

        var edits = (body?.Edits ?? [])
            .Select(e => new CatalogItemEdit(e.ItemId, e.Name, e.Description, e.PriceMinor))
            .ToList();

        await mediator.Send(
            new ReviewCatalogCommand(tenant.TenantId, catalogId, ResolveActor(actor), edits), ct);

        return Ok(new { catalog_id = catalogId, status = "in_review" });
    }

    /// <summary>Edits draft items while the catalog is in_review (no status change; repeatable).</summary>
    [HttpPost("catalogs/{catalogId}/edit")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> EditItems(
        string catalogId,
        [FromBody] EditCatalogItemsRequest body,
        [FromHeader(Name = "X-Actor")] string? actor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            return Problem(statusCode: 400, title: "Missing tenant", detail: "X-Tenant-Id header is required.");

        var edits = (body.Edits ?? [])
            .Select(e => new CatalogItemEdit(e.ItemId, e.Name, e.Description, e.PriceMinor))
            .ToList();

        await mediator.Send(
            new EditCatalogItemsCommand(tenant.TenantId, catalogId, ResolveActor(actor), edits), ct);

        return Ok(new { catalog_id = catalogId, status = "in_review", edited = edits.Count });
    }

    /// <summary>Approves a reviewed catalog (in_review → approved).</summary>
    [HttpPost("catalogs/{catalogId}/approve")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Approve(
        string catalogId,
        [FromHeader(Name = "X-Actor")] string? actor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            return Problem(statusCode: 400, title: "Missing tenant", detail: "X-Tenant-Id header is required.");

        await mediator.Send(new ApproveCatalogCommand(tenant.TenantId, catalogId, ResolveActor(actor)), ct);

        return Ok(new { catalog_id = catalogId, status = "approved" });
    }

    /// <summary>Publishes an approved catalog (approved → published) and supersedes the prior version.</summary>
    [HttpPost("catalogs/{catalogId}/publish")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Publish(
        string catalogId,
        [FromHeader(Name = "X-Actor")] string? actor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            return Problem(statusCode: 400, title: "Missing tenant", detail: "X-Tenant-Id header is required.");

        await mediator.Send(new PublishCatalogCommand(tenant.TenantId, catalogId, ResolveActor(actor)), ct);

        return Ok(new { catalog_id = catalogId, status = "published" });
    }

    private static string ResolveActor(string? actor)
        => string.IsNullOrWhiteSpace(actor) ? "owner" : actor;
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

/// <summary>A single owner item edit in a review request (null fields left unchanged).</summary>
public sealed record CatalogItemEditDto(
    [property: JsonPropertyName("item_id")] string ItemId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("price_minor")] long? PriceMinor)
{
    public CatalogItemEditDto() : this(string.Empty, null, null, null) { }
}
