using System.Text.Json.Serialization;
using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Menu.GetCatalogDraftItems;

/// <summary>
/// Owner read of a catalog's items at ANY status — the draft-scoped counterpart to the
/// published-only reads (spec 11 §5.2; docs §12 gap 4).
///
/// <para><c>POST /api/menu/catalogs/{id}/edit</c> keys on the namespaced <c>{catalogId}:{rawId}</c>
/// item id, which <c>VendorCatalogItems.ItemId</c> already stores. What was missing was any way
/// to READ those ids while the catalog is <c>in_review</c>: <c>SearchPublishedItemsAsync</c> and
/// <c>GetPublishedItemsAsync</c> both filter <c>Status == Published</c>, which by construction
/// excludes every status in which editing is legal.</para>
///
/// <para>An <see cref="IOperatorActionRequest"/> despite being a read: a draft carries
/// un-reviewed, machine-extracted content from an untrusted document, so it takes the same
/// owner gate as the rest of the D4 lifecycle and lands on the audit trail either way.</para>
/// </summary>
public sealed record GetCatalogDraftItemsQuery(
    string TenantId,
    string CatalogId,
    string Actor,
    string ReasonCode,
    string ActorRole) : IRequest<CatalogDraftItemsResult>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => Actor;

    public string RequiredRole => StaffRole.Owner;

    public string ActionName => "catalog.draft_items_read";

    public string TargetType => "vendor_catalog";

    public string TargetId => CatalogId;
}

/// <summary>
/// The catalog's own status travels with the items. That is what relieves docs §12 gap 3: until
/// now the console could only track statuses its own writes had confirmed, so it could not tell
/// <c>quarantined</c> (terminal) from "still extracting".
/// </summary>
public sealed record CatalogDraftItemsResult(
    [property: JsonPropertyName("catalog_id")] string CatalogId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version_label")] string VersionLabel,
    [property: JsonPropertyName("items")] IReadOnlyList<CatalogDraftItem> Items);

public sealed record CatalogDraftItem(
    /// The namespaced id POST .../edit keys on. A raw id here would be silently unusable.
    [property: JsonPropertyName("item_id")] string ItemId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("price_minor")] long PriceMinor,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("allergen_info")] string? AllergenInfo,
    /// How confident the extractor was. Surfaced so a reviewer can prioritise the shakiest rows.
    [property: JsonPropertyName("extraction_confidence")] double? ExtractionConfidence);

public sealed class GetCatalogDraftItemsValidator : AbstractValidator<GetCatalogDraftItemsQuery>
{
    public GetCatalogDraftItemsValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.CatalogId).NotEmpty();
        // Deliberately NO rule on Actor/ActorRole/ReasonCode. This is an operator action, so a
        // missing actor is an AUTHORIZATION failure, not a shape problem — and
        // `ValidationPipelineBehavior` runs BEFORE `OperatorActionBehavior`, so a NotEmpty rule
        // here answers 400 and returns before the behavior can deny-audit the attempt
        // (spec 14 §3.1 step 5). A caller probing this endpoint with no X-Actor would be turned
        // away leaving no record of having tried. The waitlist validators omit them for the same
        // reason.
    }
}

public sealed class GetCatalogDraftItemsQueryHandler(
    IVendorCatalogRepository catalogs,
    IAuditWriter audit)
    : IRequestHandler<GetCatalogDraftItemsQuery, CatalogDraftItemsResult>
{
    private const string Action = "catalog.draft_items_read";

    public async Task<CatalogDraftItemsResult> Handle(
        GetCatalogDraftItemsQuery request, CancellationToken ct)
    {
        var catalog = await catalogs.GetAsync(request.CatalogId, ct);
        if (catalog is null)
        {
            // Audited even when there is nothing to show: an operator probing for catalog ids
            // is exactly the pattern the trail exists to make visible.
            await WriteAuditAsync(request, "allow", found: false);
            throw new CatalogNotFoundException(request.CatalogId);
        }

        var items = await catalogs.GetDraftItemsAsync(request.CatalogId, ct);

        // OperatorActionBehavior writes only the DENY record — the ALLOW is the handler's job.
        // Without this the trail would show refusals with no matching successes, which reads as
        // "nobody ever looked" rather than "everybody who looked was allowed to".
        await WriteAuditAsync(request, "allow", found: true);

        return new CatalogDraftItemsResult(
            catalog.CatalogId,
            // The authoritative wire spelling, read from the enum's own
            // [JsonStringEnumMemberName]. A hand-rolled PascalCase→snake_case pass is correct
            // only for single-word members and drifts the moment one is not.
            EnumWireNames.ToWireName(catalog.Status),
            catalog.VersionLabel,
            [.. items.Select(i => new CatalogDraftItem(
                i.ItemId, i.Name, i.Description, i.PriceMinor, i.Currency,
                i.Category, i.AllergenInfo, i.ExtractionConfidence))]);
    }

    // CancellationToken.None: the record must be durable even if the client disconnects and
    // cancels the request token.
    private Task WriteAuditAsync(GetCatalogDraftItemsQuery query, string decision, bool found)
        => audit.WriteAsync(new AuditRecord(
            TenantId: query.TenantId,
            ActorType: StaffRole.IsKnown(query.ActorRole) ? query.ActorRole : "operator",
            ActorId: string.IsNullOrWhiteSpace(query.Actor) ? "unknown" : query.Actor,
            Action: Action,
            TargetType: "vendor_catalog",
            TargetId: query.CatalogId,
            Decision: decision,
            DataClasses: ["catalog_draft"],
            ReasonCode: string.IsNullOrWhiteSpace(query.ReasonCode) ? null : query.ReasonCode,
            CorrelationId: query.CatalogId,
            Extra: new { found }), CancellationToken.None);
}
