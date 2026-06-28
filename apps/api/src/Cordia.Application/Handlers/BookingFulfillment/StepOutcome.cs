namespace Cordia.Application.Handlers.BookingFulfillment;

/// <summary>
/// Result returned by each step handler to the driver loop (spec 06 §6.2).
/// The driver loops while the result is <see cref="Continue"/>; any other value
/// causes the loop to break and the current dispatch to finish.
/// </summary>
public enum StepOutcome
{
    /// <summary>The step advanced state; the driver should reload the operation and dispatch the next step.</summary>
    Continue,

    /// <summary>The step cannot proceed yet (e.g. waiting on external input). The driver stops; the sweeper re-enqueues later.</summary>
    Wait,

    /// <summary>The operation has reached a terminal state (Booked, Rejected, etc.). The driver stops.</summary>
    Terminal,

    /// <summary>The step needs to be rescheduled (e.g. circuit open, transient failure with back-off). Driver stops.</summary>
    Reschedule,
}
