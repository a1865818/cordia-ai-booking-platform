using Cordia.Application.Messaging;
using MediatR;

namespace Cordia.Application.Handlers.Waitlist.ReconcileAccept;

/// <summary>
/// Reconciles a waitlist accept whose <c>Offered → Accepted</c> claim was LOST to a concurrent
/// expire/decline AFTER the booking had already been minted through spec 04 (spec 11 §5.1).
///
/// <para>Accept and auto-accept are record-before-state (mint the booking, then claim the offer) for
/// crash-safety (Inv-5). If an expire/decline wins the version-guarded claim in the window between the
/// mint and the claim, a real <c>booking_operations</c> record exists while the offer is now terminal
/// (expired/declined), the hold was released, and — because the offer is no longer live — the enrollee
/// would be eligible for a future re-match (a potential double-book across slots). This message probes
/// the minted operation's real outcome and settles the divergence: if the operation actually booked,
/// the enrollment is marked terminal (fulfilled) so it is never re-offered; if it failed (e.g. the
/// released hold could not be re-reserved), nothing was booked so the enrollee correctly stays eligible.</para>
///
/// <para>Scheduled with a delay (via <see cref="WaitlistAcceptReconcileScheduler"/>) so the spec-06
/// engine has finished the operation before the probe. Implements <see cref="IInboxConsumerRequest"/>
/// so all writes run in ONE inbox transaction (consumer <c>"waitlist.processor"</c>, Inv-5); the
/// handler MUST NOT open its own transaction and settles idempotently on redelivery.</para>
/// </summary>
public sealed record ReconcileWaitlistAcceptCommand(
    string TenantId,
    string MessageId,
    string? CorrelationId,
    string OfferId,
    string OperationId,
    string EnrollmentId,
    int Attempt = 0) : IRequest<Unit>, IInboxConsumerRequest
{
    /// <summary>Queue message type on the waitlist lane that maps to this command.</summary>
    public const string MessageType = "waitlist.commands.reconcile_accept.v1";

    public string ConsumerName => "waitlist.processor";
    string IInboxConsumerRequest.MessageId => MessageId;
    string IInboxConsumerRequest.TenantId => TenantId;
    string? IInboxConsumerRequest.CorrelationId => CorrelationId;
    string? IInboxConsumerRequest.OperationId => OperationId;
}
