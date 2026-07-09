using Cordia.Application.Common.Authorization;
using MediatR;

namespace Cordia.Application.Handlers.Menu.UploadMenu;

/// <summary>
/// Owner upload of a menu artifact (spec 11 §5.2). Creates a new <c>uploaded</c> vendor-catalog row
/// and enqueues a <c>menu.commands.ingest.v1</c> outbox message so the isolated ingestion consumer
/// (<c>menu.processor</c>) picks it up asynchronously. Returns the new catalog id.
///
/// <para>This is a synchronous HTTP command (NOT an inbox consumer): it opens its own
/// <see cref="Cordia.Domain.Interfaces.IUnitOfWork"/> transaction so the catalog row + outbox
/// message + audit event commit atomically (Inv-4).</para>
/// </summary>
public sealed record UploadMenuCommand(
    string TenantId,
    string Actor,
    string SourceArtifactRef,
    string VersionLabel,
    string ReasonCode) : IRequest<string>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => Actor;

    public string ActionName => "catalog.uploaded";

    public string TargetType => "vendor_catalog";

    public string TargetId => SourceArtifactRef;
}
