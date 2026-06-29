namespace Cordia.Domain.Fulfillment;

/// <summary>Policy action chosen by the retry classifier for a failed provider call.</summary>
public enum RetryAction
{
    /// <summary>Schedule another attempt after full-jitter backoff (§15.2).</summary>
    ScheduleRetry,

    /// <summary>Enter the §16.2 reconciliation order before deciding whether to re-create.</summary>
    Reconcile,

    /// <summary>No capacity available — route to AlternativesRequired (business outcome, not a retry).</summary>
    NoCapacity,

    /// <summary>Await a user action (payment, verification) before proceeding.</summary>
    AwaitUser,

    /// <summary>Terminal failure — route to Rejected or FailedFinal.</summary>
    Terminal,

    /// <summary>Budget exhausted or auth/permanent failure — route to ManualReview.</summary>
    ManualReview,

    /// <summary>Internal defect (unexpected exception from adapter/transport) — route to DLQ.</summary>
    Poison,
}
