using Cordia.Domain.Interfaces;

namespace Cordia.Application.Handlers.Preorder.Recovery;

/// <summary>
/// Crash-recovery sweep for stuck pre-order sagas (spec 11 §5.3, FM#8-11). Finds non-terminal sagas
/// whose <c>UpdatedAt</c> is older than a threshold (i.e. a step crashed or its hand-off was lost)
/// and enqueues a <c>preorder.commands.reconcile.v1</c> message for each, which re-drives the step
/// matching the persisted state — idempotent re-entry that never re-acquires anything already done
/// (e.g. a saga past AuthorizingPayment is resumed at its CURRENT step, never re-authorized).
///
/// <para>Stateless coordinator over the repository + outbox; wire to a scheduled job or a periodic
/// hosted-service (consistent with the existing schedulers) to run it on a cadence.</para>
/// </summary>
public sealed class PreorderRecoverySweeper(
    IPreorderSagaRepository sagaRepo,
    IOutboxRepository outboxRepo) : IPreorderRecoverySweeper
{
    /// <summary>
    /// Enqueues a reconcile message for every saga stuck since before <paramref name="olderThan"/>.
    /// Returns the number of sagas swept. Each reconcile is idempotent on the consumer side, so a
    /// repeated sweep of a still-stuck saga is harmless.
    /// </summary>
    public async Task<int> SweepAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var stuck = await sagaRepo.FindStuckAsync(olderThan, ct);

        foreach (var saga in stuck)
        {
            await outboxRepo.AddAsync(
                PreorderSagaMessages.StepMessage(
                    saga.TenantId, PreorderSagaMessages.Reconcile, saga.SagaId, saga.OperationId, now), ct);
        }

        return stuck.Count;
    }
}
