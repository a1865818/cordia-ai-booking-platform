using System.Text.Json;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.AdvanceOperation;

/// <summary>
/// Queue-driven command that drives a booking operation through successive workflow steps until
/// it reaches a terminal state or a point where it must wait (spec 06 §6.2).
///
/// This is a PLAIN IRequest — it intentionally does NOT implement IInboxConsumerRequest.
/// The InboxDeduplicationBehavior would wrap the entire multi-step loop in a single transaction,
/// which would forbid the mid-handler external provider call (Inv-4 / §6.2 TXN1→external→TXN2
/// protocol). Best-effort inbox admission is handled manually inside the handler (§12.6).
///
/// <para>T11: <see cref="MessageType"/> and <see cref="Payload"/> are carried from the original
/// transport envelope so the driver can reconstruct a <c>JobEnvelope</c> for DLQ quarantine
/// without referencing the envelope directly (Inv-14, §17.2).</para>
/// </summary>
/// <param name="TenantId">Tenant that owns the operation.</param>
/// <param name="OperationId">The operation to advance.</param>
/// <param name="MessageId">Original outbox message_id used for best-effort inbox dedup.</param>
/// <param name="MessageType">Original transport message type (e.g. <c>booking.command.requested.v1</c>).</param>
/// <param name="Payload">Original transport payload element (used when reconstructing DLQ envelope).</param>
public sealed record AdvanceBookingOperationCommand(
    string TenantId,
    string OperationId,
    string MessageId,
    string MessageType = "",
    JsonElement Payload = default) : IRequest<Unit>;
