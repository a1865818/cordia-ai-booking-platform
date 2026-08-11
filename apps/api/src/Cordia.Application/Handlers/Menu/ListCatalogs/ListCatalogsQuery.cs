using System.Text.Json.Serialization;
using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Menu.ListCatalogs;

/// <summary>
/// Owner discovery read over the tenant's vendor catalogs (docs §12 gap 3).
///
/// <para>Until this existed the console could only list catalogs it had started itself, in this
/// browser session — so an ingestion begun on another machine, or before a refresh, was simply
/// invisible, and a catalog stuck in <c>extracting</c> could not be told from one that had never
/// been registered.</para>
///
/// <para>Owner-gated and audited like the rest of the D4 lifecycle: a catalog list exposes what
/// a venue is in the middle of publishing, including drafts extracted from untrusted documents.</para>
/// </summary>
public sealed record ListCatalogsQuery(
    string TenantId,
    string Actor,
    string ReasonCode,
    string ActorRole,
    int Limit = 50) : IRequest<CatalogListResult>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => Actor;

    public string RequiredRole => StaffRole.Owner;

    public string ActionName => "catalog.list_read";

    public string TargetType => "vendor_catalog";

    public string TargetId => "*";
}

public sealed record CatalogListResult(
    [property: JsonPropertyName("catalogs")] IReadOnlyList<CatalogSummary> Catalogs);

public sealed record CatalogSummary(
    [property: JsonPropertyName("catalog_id")] string CatalogId,
    [property: JsonPropertyName("version_label")] string VersionLabel,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    /// <summary>
    /// How long the catalog has been at its CURRENT status, in seconds — measured from
    /// <c>status_changed_at</c>, not from creation, so a catalog that waited in a queue backlog
    /// before extraction started does not present as already stale the moment it begins.
    ///
    /// <para>The console's <c>looksStuck</c> threshold and the server-side
    /// <c>MenuIngestionMonitor</c> read the same clock deliberately: the monitor now settles a
    /// stalled catalog into the terminal <c>extract_failed</c> (D4 amendment, owner ruling
    /// 2026-08-06), so operator and server disagreeing about the age of the same catalog would
    /// mean the console still showing "extracting" for a row the server had already failed.</para>
    /// </summary>
    [property: JsonPropertyName("age_seconds")] long AgeSeconds);

public sealed class ListCatalogsValidator : AbstractValidator<ListCatalogsQuery>
{
    public ListCatalogsValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        // Deliberately NO rule on Actor/ActorRole/ReasonCode. This is an operator action, so a
        // missing actor is an AUTHORIZATION failure, not a shape problem — and
        // `ValidationPipelineBehavior` runs BEFORE `OperatorActionBehavior`, so a NotEmpty rule
        // here answers 400 and returns before the behavior can deny-audit the attempt
        // (spec 14 §3.1 step 5). A caller probing this endpoint with no X-Actor would be turned
        // away leaving no record of having tried. The waitlist validators omit them for the same
        // reason.
        RuleFor(x => x.Limit).InclusiveBetween(1, 200);
    }
}

public sealed class ListCatalogsQueryHandler(
    IVendorCatalogRepository catalogs,
    IAuditWriter audit)
    : IRequestHandler<ListCatalogsQuery, CatalogListResult>
{
    public async Task<CatalogListResult> Handle(ListCatalogsQuery request, CancellationToken ct)
    {
        var rows = await catalogs.ListAsync(request.Limit, ct);
        var now = DateTimeOffset.UtcNow;

        // OperatorActionBehavior writes only the DENY record; the ALLOW is the handler's job.
        await audit.WriteAsync(new AuditRecord(
            TenantId: request.TenantId,
            ActorType: StaffRole.IsKnown(request.ActorRole) ? request.ActorRole : "operator",
            ActorId: string.IsNullOrWhiteSpace(request.Actor) ? "unknown" : request.Actor,
            Action: "catalog.list_read",
            TargetType: "vendor_catalog",
            TargetId: "*",
            Decision: "allow",
            DataClasses: ["catalog_draft"],
            ReasonCode: string.IsNullOrWhiteSpace(request.ReasonCode) ? null : request.ReasonCode,
            CorrelationId: request.TenantId,
            Extra: new { count = rows.Count }), CancellationToken.None);

        return new CatalogListResult(
        [
            .. rows.Select(c => new CatalogSummary(
                c.CatalogId,
                c.VersionLabel,
                EnumWireNames.ToWireName(c.Status),
                c.CreatedAt,
                // Clamped at zero: a clock skew must not produce a negative age the console
                // would render as a countdown.
                (long)Math.Max(0, (now - c.StatusChangedAt).TotalSeconds))),
        ]);
    }
}
