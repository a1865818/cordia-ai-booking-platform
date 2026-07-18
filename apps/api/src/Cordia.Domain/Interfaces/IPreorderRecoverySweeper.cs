namespace Cordia.Domain.Interfaces;

/// <summary>
/// Domain seam for the crash-recovery sweep of stuck pre-order sagas (spec 11 §5.3, divergence #3).
///
/// Decouples the <c>PreorderRecoverySweeperHostedService</c> in Infrastructure from the Application
/// implementation, mirroring <see cref="INotificationReconciler"/> / <see cref="ISlaEvaluator"/>.
/// </summary>
public interface IPreorderRecoverySweeper
{
    /// <summary>
    /// Enqueues a <c>preorder.commands.reconcile.v1</c> message for every non-terminal saga whose
    /// last update is older than <paramref name="olderThan"/> (a crashed step or lost hand-off).
    /// Returns the number of sagas swept. Each reconcile is idempotent on the consumer side, so a
    /// repeated sweep of a still-stuck saga is harmless.
    /// </summary>
    Task<int> SweepAsync(DateTimeOffset olderThan, CancellationToken ct = default);
}
