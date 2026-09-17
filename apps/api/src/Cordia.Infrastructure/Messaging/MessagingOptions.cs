namespace Cordia.Infrastructure.Messaging;

/// <summary>
/// Root options for the Infrastructure messaging subsystem (§7, T3).
/// Bound from the "Messaging" configuration section.
/// </summary>
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    public OutboxPublisherOptions Publisher { get; set; } = new();

    public ScheduledJobPollerOptions ScheduledJobPoller { get; set; } = new();

    public StepSweeperOptions StepSweeper { get; set; } = new();

    public SlaOptions Sla { get; set; } = new();

    public NotificationReconcileOptions NotificationReconcile { get; set; } = new();

    public MissingNotificationAuditOptions MissingNotificationAudit { get; set; } = new();

    public MetricsGaugeOptions MetricsGauge { get; set; } = new();

    public PreorderRecoveryOptions PreorderRecovery { get; set; } = new();

    public RetentionSweepOptions RetentionSweep { get; set; } = new();

    public MenuIngestionMonitorOptions MenuIngestionMonitor { get; set; } = new();
}

/// <summary>Options for the stalled-menu-ingestion monitor (docs §12 gap 8).</summary>
public sealed class MenuIngestionMonitorOptions
{
    /// <summary>Set to false to disable the monitor (e.g. in test environments).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Milliseconds between ticks. Default: 5 minutes — this is a reporter, not a recovery path.</summary>
    public int IntervalMs { get; set; } = 300000;

    /// <summary>
    /// How long a catalog may sit in scanning/extracting before it is reported as stalled.
    /// Default 900 s, matching the console's own <c>STALE_INGESTION_SECONDS</c> so the operator and
    /// the server never disagree about whether the same catalog is stuck.
    /// </summary>
    public int StalenessSeconds { get; set; } = 900;
}

/// <summary>Options for the pre-order saga recovery sweeper hosted service (spec 11 §5.3, divergence #3).</summary>
public sealed class PreorderRecoveryOptions
{
    /// <summary>Set to false to disable the sweeper (e.g. in test environments).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Milliseconds between sweeper ticks. Safety-net cadence (mirrors the step sweeper). Default: 30 s.
    /// </summary>
    public int IntervalMs { get; set; } = 30000;

    /// <summary>
    /// A non-terminal saga must have been untouched for at least this many seconds before it is
    /// considered stuck and eligible for a reconcile sweep. Must exceed the longest per-step timeout
    /// window so the primary reconcile timer fires first; the sweeper is the backstop. Default: 900 s.
    /// </summary>
    public int StalenessSeconds { get; set; } = 900;
}

/// <summary>Options for the scheduled-job poller hosted service (§7, T5).</summary>
public sealed class ScheduledJobPollerOptions
{
    /// <summary>Set to false to disable the poller (e.g. in test environments).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Milliseconds between poller ticks. §7 targets ≤1s dispatch precision past run_after;
    /// 500 ms leaves headroom for per-tick processing so the worst-case latency stays under 1s.
    /// </summary>
    public int PollIntervalMs { get; set; } = 500;

    /// <summary>Maximum number of scheduled jobs to claim per tick.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// Seconds a claim lease is held before another poller may reclaim the job.
    /// Default: 30 s.
    ///
    /// <para>Deliberately NOT the same as <see cref="OutboxPublisherOptions.LeaseSeconds"/>
    /// (300 s). That lane carries menu extraction, whose provider call is bounded at 120 s;
    /// this one dispatches scheduled jobs and has no such step. Do not "align" them.</para>
    /// </summary>
    public int LeaseSeconds { get; set; } = 30;
}

/// <summary>Options for the step sweeper hosted service (§7, T6).</summary>
public sealed class StepSweeperOptions
{
    /// <summary>Set to false to disable the sweeper (e.g. in test environments).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Milliseconds between sweeper ticks. §7 default 30 s safety-net cadence.
    /// </summary>
    public int IntervalMs { get; set; } = 30000;

    /// <summary>Maximum number of stale non-terminal steps to re-enqueue per tick.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// A step must have been untouched for at least this many seconds before it is
    /// considered stale and eligible for sweeping.  Default: 60 s.
    /// </summary>
    public int StalenessSeconds { get; set; } = 60;
}

/// <summary>Options for the SLA evaluator hosted service (§8, T9).</summary>
public sealed class SlaOptions
{
    /// <summary>Set to false to disable the SLA evaluator (e.g. in test environments).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Milliseconds between evaluator ticks. Default: 2000 ms.</summary>
    public int IntervalMs { get; set; } = 2000;

    /// <summary>Maximum number of non-terminal operations to evaluate per tick.</summary>
    public int BatchSize { get; set; } = 100;
}

/// <summary>Options for the privacy retention sweep hosted service (spec 14 §24.3, spec 12 §6.3).</summary>
public sealed class RetentionSweepOptions
{
    /// <summary>Set to false to disable the retention sweeper (e.g. in test environments).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Hours between sweep cycles. Retention is jurisdiction-window work — daily is plenty.</summary>
    public int IntervalHours { get; set; } = 24;

    /// <summary>
    /// Seconds to wait after startup before the first sweep, so boot traffic isn't competing
    /// with a cross-tenant scan.
    /// </summary>
    public int InitialDelaySeconds { get; set; } = 300;
}

/// <summary>Options for the notification reconcile hosted service (spec 10 §12.8, T4).</summary>
public sealed class NotificationReconcileOptions
{
    /// <summary>Set to false to disable the reconciler (e.g. in test environments).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Milliseconds between reconciler ticks. Default: 2000 ms.</summary>
    public int PollIntervalMs { get; set; } = 2000;

    /// <summary>Maximum number of stuck-Sending notifications to process per tick.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// A notification must have been in <c>Sending</c> for at least this many seconds before
    /// it is eligible for reconciliation (the §12.8 ambiguous window threshold). Default: 30 s.
    /// </summary>
    public int StuckThresholdSeconds { get; set; } = 30;
}

/// <summary>Options for the missing-terminal-notification audit hosted service (spec 10 §25.4 / FM#12, T5b).</summary>
public sealed class MissingNotificationAuditOptions
{
    /// <summary>Set to false to disable the auditor (e.g. in test environments).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Milliseconds between auditor ticks. Default: 5000 ms.</summary>
    public int PollIntervalMs { get; set; } = 5000;

    /// <summary>Maximum number of terminal operations to audit per tick.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// A terminal booking operation must have been in its terminal state for at least this many
    /// seconds before it is eligible for the alert sweep. Allows a brief settle window for
    /// in-flight notifications to land before declaring them missing. Default: 120 s.
    /// </summary>
    public int TerminalAgeSeconds { get; set; } = 120;
}

/// <summary>Options for the metrics gauge hosted service (spec 13 §25.2, T5).</summary>
public sealed class MetricsGaugeOptions
{
    /// <summary>Set to false to disable the gauge poller (e.g. in test environments).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between gauge refresh ticks. Default: 15 s.</summary>
    public int IntervalSeconds { get; set; } = 15;
}

/// <summary>Options for the outbox publisher hosted service (§7).</summary>
public sealed class OutboxPublisherOptions
{
    /// <summary>Set to false to disable the publisher (e.g. in test environments).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Milliseconds between publisher ticks (§7: 100–250 ms short tick).
    /// Default: 250 ms.
    /// </summary>
    public int PollIntervalMs { get; set; } = 250;

    /// <summary>Maximum number of outbox rows to claim per tick.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// Seconds a claim lease is held before another publisher may reclaim the row (§5.2/§8).
    /// Default: 300 s.
    ///
    /// <para>Sizes spec 05 §12 <b>OD-3</b>, which proposed "lease = 2× p99 step latency,
    /// heartbeat-renewable" and never committed to a number. The slowest consumer on any lane is
    /// menu ingestion, whose provider call is bounded by
    /// <c>LangGraphOptions.ExtractTimeoutSeconds</c> (120 s) and runs inside the consumer's inbox
    /// transaction. 300 s is 2.5× that: enough for ONE worst-case extraction to finish inside the
    /// lease covering it.</para>
    ///
    /// <para><b>A sized lease is renewed as the lane runs (#102).</b> The claim stamps one expiry on
    /// the whole batch, while rows within a lane publish strictly sequentially — so without renewal
    /// N same-lane rows shared one lease: three 120 s <c>menu.commands</c> rows run 0–120, 120–240 and
    /// 240–360 s, so the third outruns a 300 s lease and a fourth would start after it expired.
    /// <c>OutboxPublisherHostedService.PublishLaneAsync</c> renews the current row and every row queued
    /// behind it whenever the lane's lease falls to half of this value — a bounded heartbeat, so this
    /// number stays a per-row budget however many rows queue, and a fast lane writes nothing. That is
    /// OD-3's renewal half; a crashed worker still releases its rows within one
    /// <c>LeaseSeconds</c>.</para>
    ///
    /// <para><b>The lease was never the correctness boundary.</b> Spec 05 §12.5: leases "prevent
    /// <i>simultaneous</i> processing only; they do not replace ... inbox dedup". A single extraction
    /// that outruns its own lease is still recovered by that second layer — a reclaiming
    /// publisher's <c>TryAdmitAsync</c> INSERT blocks on the first worker's uncommitted inbox row,
    /// then suppresses the duplicate. Renewal removes the routine case in which that happened to
    /// healthy rows, and with it the spurious <c>publish_attempts</c> increments.</para>
    ///
    /// <para><b>There are two <c>LeaseSeconds</c> in this file and they are not the same knob.</b>
    /// This one is the outbox publisher's, at 300 s.
    /// <see cref="ScheduledJobPollerOptions.LeaseSeconds"/> governs scheduled-job redelivery on a
    /// different lane and stays at 30 s. Do not "align" them.</para>
    /// </summary>
    public int LeaseSeconds { get; set; } = 300;

    /// <summary>
    /// Max claim/publish attempts before a row is treated as poison and dead-lettered (instead of
    /// being reclaimed forever). An unroutable message_type is dead-lettered immediately regardless.
    /// Default: 10.
    /// </summary>
    public int MaxPublishAttempts { get; set; } = 10;
}
