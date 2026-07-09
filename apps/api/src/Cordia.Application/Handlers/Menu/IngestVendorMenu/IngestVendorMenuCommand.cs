using Cordia.Application.Messaging;
using MediatR;

namespace Cordia.Application.Handlers.Menu.IngestVendorMenu;

/// <summary>
/// Queue-driven command for the menu-ingestion pipeline (spec 11 §5.2): drives a catalog
/// <c>uploaded → scanning → {quarantined | extracting → draft_ready}</c>.
///
/// <para>Implements <see cref="IInboxConsumerRequest"/> so <c>InboxDeduplicationBehavior</c>
/// wraps all writes in ONE transaction (consumer <c>"menu.processor"</c>, Inv-5). The handler
/// MUST NOT open its own transaction.</para>
///
/// <para>This is the realization of the <c>menu.commands.ingest.v1</c> queue contract
/// (<c>libs/shared-schemas/schemas/job-events/menu.commands.ingest.v1.json</c>).</para>
/// </summary>
public sealed record IngestVendorMenuCommand(
    string TenantId,
    string CatalogId,
    string SourceArtifactRef,
    string MessageId,
    string? CorrelationId)
    : IRequest<Unit>, IInboxConsumerRequest
{
    public string ConsumerName => "menu.processor";
    string IInboxConsumerRequest.MessageId => MessageId;
    string IInboxConsumerRequest.TenantId => TenantId;
    string? IInboxConsumerRequest.CorrelationId => CorrelationId;

    /// <summary>Menu ingestion is not tied to a booking operation.</summary>
    string? IInboxConsumerRequest.OperationId => null;
}
