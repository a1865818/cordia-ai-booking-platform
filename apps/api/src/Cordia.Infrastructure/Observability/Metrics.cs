using System.Diagnostics.Metrics;
using Cordia.Domain.Observability;

namespace Cordia.Infrastructure.Observability;

/// <summary>
/// Singleton that owns the <see cref="Meter"/> and every pre-declared instrument for the §4.2
/// metric registry. Inject this class to record agent/fulfillment/notification measurements.
/// Dispose it on application shutdown to flush the meter.
/// </summary>
public sealed class Metrics : IDisposable
{
    /// <summary>The shared meter backing every instrument in this class.</summary>
    public Meter Meter { get; }

    // -------------------------------------------------------------------------
    // Agent instruments
    // -------------------------------------------------------------------------

    /// <summary>Time to first response per agent turn.</summary>
    public Histogram<double> AgentTurnTimeToFirstResponseMs { get; }

    /// <summary>Per-turn end-to-end latency.</summary>
    public Histogram<double> AgentTurnLatencyMs { get; }

    /// <summary>Barge-in events that successfully interrupted the agent.</summary>
    public Counter<long> AgentBargeinSuccessTotal { get; }

    /// <summary>Total barge-in attempts.</summary>
    public Counter<long> AgentBargeinAttemptTotal { get; }

    /// <summary>Session reconnects.</summary>
    public Counter<long> AgentSessionReconnectTotal { get; }

    /// <summary>STT/ASR confidence score by field type.</summary>
    public Histogram<double> AgentSttConfidence { get; }

    /// <summary>Clarification prompts issued, labelled by reason.</summary>
    public Counter<long> AgentClarificationTotal { get; }

    /// <summary>Spell-back prompts issued.</summary>
    public Counter<long> AgentSpellbackTotal { get; }

    /// <summary>Draft validation failures, labelled by field.</summary>
    public Counter<long> AgentDraftValidationFailureTotal { get; }

    /// <summary>Confirmation flow abandonments.</summary>
    public Counter<long> AgentConfirmationAbandonmentTotal { get; }

    /// <summary>Tool errors, labelled by tool and category.</summary>
    public Counter<long> AgentToolErrorTotal { get; }

    /// <summary>Tool timeouts, labelled by tool.</summary>
    public Counter<long> AgentToolTimeoutTotal { get; }

    /// <summary>Human-handoff events, labelled by reason.</summary>
    public Counter<long> AgentHandoffTotal { get; }

    // -------------------------------------------------------------------------
    // Fulfillment instruments
    // -------------------------------------------------------------------------

    /// <summary>Accepted-to-held latency.</summary>
    public Histogram<double> FulfillmentAcceptedToHeldMs { get; }

    /// <summary>Accepted-to-booked latency by provider.</summary>
    public Histogram<double> FulfillmentAcceptedToBookedMs { get; }

    /// <summary>Capacity conditional-write race losses.</summary>
    public Counter<long> FulfillmentCapacityRaceLossTotal { get; }

    /// <summary>Capacity holds successfully acquired.</summary>
    public Counter<long> FulfillmentHoldAcquiredTotal { get; }

    /// <summary>Capacity holds that expired before finalisation.</summary>
    public Counter<long> FulfillmentHoldExpiredTotal { get; }

    /// <summary>Operations reaching a terminal state, labelled by state.</summary>
    public Counter<long> FulfillmentOperationTerminalTotal { get; }

    /// <summary>Retry attempts, labelled by category.</summary>
    public Counter<long> FulfillmentRetryAttemptTotal { get; }

    /// <summary>Elapsed time across retries for a single operation.</summary>
    public Histogram<double> FulfillmentRetryElapsedMs { get; }

    /// <summary>Operations with an ambiguous provider outcome.</summary>
    public Counter<long> FulfillmentAmbiguousOutcomeTotal { get; }

    /// <summary>Reconciliation events, labelled by result.</summary>
    public Counter<long> FulfillmentReconciliationTotal { get; }

    /// <summary>Duplicate message deliveries detected.</summary>
    public Counter<long> FulfillmentDeliveryDuplicateTotal { get; }

    /// <summary>Inbox records suppressed by deduplication.</summary>
    public Counter<long> FulfillmentInboxDedupSuppressedTotal { get; }

    /// <summary>Provider idempotency-key conflicts, labelled by provider.</summary>
    public Counter<long> FulfillmentProviderIdempotencyConflictTotal { get; }

    // Gauges (fulfillment.dlq.*, fulfillment.manual_review.*) are registered as ObservableGauges
    // in the metric-emission ticket via the public Meter.

    // -------------------------------------------------------------------------
    // Gauge cached values (updated by MetricsGaugeHostedService; read by ObservableGauge callbacks)
    // -------------------------------------------------------------------------

    private long _dlqDepth;
    private long _dlqOldestAgeSecondsRaw; // bits of a double
    private long _manualReviewDepth;
    private long _manualReviewOldestAgeSecondsRaw; // bits of a double

    /// <summary>Cached DLQ depth; updated atomically by MetricsGaugeHostedService.</summary>
    public long DlqDepth
    {
        get => Interlocked.Read(ref _dlqDepth);
        set => Interlocked.Exchange(ref _dlqDepth, value);
    }

    /// <summary>Cached age of oldest DLQ item in seconds.</summary>
    public double DlqOldestAgeSeconds
    {
        get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _dlqOldestAgeSecondsRaw));
        set => Interlocked.Exchange(ref _dlqOldestAgeSecondsRaw, BitConverter.DoubleToInt64Bits(value));
    }

    /// <summary>Cached manual-review work-item depth.</summary>
    public long ManualReviewDepth
    {
        get => Interlocked.Read(ref _manualReviewDepth);
        set => Interlocked.Exchange(ref _manualReviewDepth, value);
    }

    /// <summary>Cached age of oldest manual-review work item in seconds.</summary>
    public double ManualReviewOldestAgeSeconds
    {
        get => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _manualReviewOldestAgeSecondsRaw));
        set => Interlocked.Exchange(ref _manualReviewOldestAgeSecondsRaw, BitConverter.DoubleToInt64Bits(value));
    }

    /// <summary>Compensation attempts that succeeded.</summary>
    public Counter<long> FulfillmentCompensationSuccessTotal { get; }

    /// <summary>Compensation attempts that failed.</summary>
    public Counter<long> FulfillmentCompensationFailureTotal { get; }

    // -------------------------------------------------------------------------
    // Notification instruments
    // -------------------------------------------------------------------------

    /// <summary>Duplicate notifications suppressed.</summary>
    public Counter<long> NotificationDuplicateSuppressedTotal { get; }

    /// <summary>Notifications accepted by the downstream provider.</summary>
    public Counter<long> NotificationProviderAcceptedTotal { get; }

    /// <summary>Notifications confirmed delivered to the recipient.</summary>
    public Counter<long> NotificationDeliveredTotal { get; }

    /// <summary>Notifications in a pending-status state.</summary>
    public Counter<long> NotificationPendingStatusTotal { get; }

    /// <summary>Notifications rejected due to a permanent invalid-recipient error.</summary>
    public Counter<long> NotificationInvalidRecipientTotal { get; }

    // -------------------------------------------------------------------------
    // Telemetry self-monitoring instruments
    // -------------------------------------------------------------------------

    /// <summary>Records processed that lacked a non-null correlation_id.</summary>
    public Counter<long> TelemetryUncorrelatedRecordTotal { get; }

    /// <summary>Telemetry data points dropped during export.</summary>
    public Counter<long> TelemetryExportDroppedTotal { get; }

    // -------------------------------------------------------------------------
    // Alert-signal instruments (spec 13 §6 / §25.4)
    // -------------------------------------------------------------------------

    /// <summary>Idempotency key reused with a different payload (Inv-3).</summary>
    public Counter<long> AcceptanceIdempotencyConflictTotal { get; }

    /// <summary>First-party capacity overcommit / negative-capacity violation (Inv-6).</summary>
    public Counter<long> FulfillmentCapacityInvariantViolationTotal { get; }

    /// <summary>Access attempted for an operation belonging to another tenant (Inv-13).</summary>
    public Counter<long> SecurityTenantCrossScopeAttemptTotal { get; }

    /// <summary>Terminal operation lacked a notification past threshold (Inv-8/Inv-9).</summary>
    public Counter<long> NotificationMissingTerminalTotal { get; }

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public Metrics()
    {
        Meter = new Meter(MetricNames.MeterName);

        // Agent
        AgentTurnTimeToFirstResponseMs = Meter.CreateHistogram<double>(
            MetricNames.AgentTurnTimeToFirstResponseMs,
            unit: "ms",
            description: "Time to first response per agent turn.");

        AgentTurnLatencyMs = Meter.CreateHistogram<double>(
            MetricNames.AgentTurnLatencyMs,
            unit: "ms",
            description: "Per-turn end-to-end latency.");

        AgentBargeinSuccessTotal = Meter.CreateCounter<long>(
            MetricNames.AgentBargeinSuccessTotal,
            unit: "1",
            description: "Barge-in events that successfully interrupted the agent.");

        AgentBargeinAttemptTotal = Meter.CreateCounter<long>(
            MetricNames.AgentBargeinAttemptTotal,
            unit: "1",
            description: "Total barge-in attempts.");

        AgentSessionReconnectTotal = Meter.CreateCounter<long>(
            MetricNames.AgentSessionReconnectTotal,
            unit: "1",
            description: "Session reconnects.");

        AgentSttConfidence = Meter.CreateHistogram<double>(
            MetricNames.AgentSttConfidence,
            unit: "1",
            description: "STT/ASR confidence score by field type.");

        AgentClarificationTotal = Meter.CreateCounter<long>(
            MetricNames.AgentClarificationTotal,
            unit: "1",
            description: "Clarification prompts issued, labelled by reason.");

        AgentSpellbackTotal = Meter.CreateCounter<long>(
            MetricNames.AgentSpellbackTotal,
            unit: "1",
            description: "Spell-back prompts issued.");

        AgentDraftValidationFailureTotal = Meter.CreateCounter<long>(
            MetricNames.AgentDraftValidationFailureTotal,
            unit: "1",
            description: "Draft validation failures, labelled by field.");

        AgentConfirmationAbandonmentTotal = Meter.CreateCounter<long>(
            MetricNames.AgentConfirmationAbandonmentTotal,
            unit: "1",
            description: "Confirmation flow abandonments.");

        AgentToolErrorTotal = Meter.CreateCounter<long>(
            MetricNames.AgentToolErrorTotal,
            unit: "1",
            description: "Tool errors, labelled by tool and category.");

        AgentToolTimeoutTotal = Meter.CreateCounter<long>(
            MetricNames.AgentToolTimeoutTotal,
            unit: "1",
            description: "Tool timeouts, labelled by tool.");

        AgentHandoffTotal = Meter.CreateCounter<long>(
            MetricNames.AgentHandoffTotal,
            unit: "1",
            description: "Human-handoff events, labelled by reason.");

        // Fulfillment
        FulfillmentAcceptedToHeldMs = Meter.CreateHistogram<double>(
            MetricNames.FulfillmentAcceptedToHeldMs,
            unit: "ms",
            description: "Accepted-to-held latency.");

        FulfillmentAcceptedToBookedMs = Meter.CreateHistogram<double>(
            MetricNames.FulfillmentAcceptedToBookedMs,
            unit: "ms",
            description: "Accepted-to-booked latency by provider.");

        FulfillmentCapacityRaceLossTotal = Meter.CreateCounter<long>(
            MetricNames.FulfillmentCapacityRaceLossTotal,
            unit: "1",
            description: "Capacity conditional-write race losses.");

        FulfillmentHoldAcquiredTotal = Meter.CreateCounter<long>(
            MetricNames.FulfillmentHoldAcquiredTotal,
            unit: "1",
            description: "Capacity holds successfully acquired.");

        FulfillmentHoldExpiredTotal = Meter.CreateCounter<long>(
            MetricNames.FulfillmentHoldExpiredTotal,
            unit: "1",
            description: "Capacity holds that expired before finalisation.");

        FulfillmentOperationTerminalTotal = Meter.CreateCounter<long>(
            MetricNames.FulfillmentOperationTerminalTotal,
            unit: "1",
            description: "Operations reaching a terminal state, labelled by state.");

        FulfillmentRetryAttemptTotal = Meter.CreateCounter<long>(
            MetricNames.FulfillmentRetryAttemptTotal,
            unit: "1",
            description: "Retry attempts, labelled by category.");

        FulfillmentRetryElapsedMs = Meter.CreateHistogram<double>(
            MetricNames.FulfillmentRetryElapsedMs,
            unit: "ms",
            description: "Elapsed time across retries for a single operation.");

        FulfillmentAmbiguousOutcomeTotal = Meter.CreateCounter<long>(
            MetricNames.FulfillmentAmbiguousOutcomeTotal,
            unit: "1",
            description: "Operations with an ambiguous provider outcome.");

        FulfillmentReconciliationTotal = Meter.CreateCounter<long>(
            MetricNames.FulfillmentReconciliationTotal,
            unit: "1",
            description: "Reconciliation events, labelled by result.");

        FulfillmentDeliveryDuplicateTotal = Meter.CreateCounter<long>(
            MetricNames.FulfillmentDeliveryDuplicateTotal,
            unit: "1",
            description: "Duplicate message deliveries detected.");

        FulfillmentInboxDedupSuppressedTotal = Meter.CreateCounter<long>(
            MetricNames.FulfillmentInboxDedupSuppressedTotal,
            unit: "1",
            description: "Inbox records suppressed by deduplication.");

        FulfillmentProviderIdempotencyConflictTotal = Meter.CreateCounter<long>(
            MetricNames.FulfillmentProviderIdempotencyConflictTotal,
            unit: "1",
            description: "Provider idempotency-key conflicts, labelled by provider.");

        FulfillmentCompensationSuccessTotal = Meter.CreateCounter<long>(
            MetricNames.FulfillmentCompensationSuccessTotal,
            unit: "1",
            description: "Compensation attempts that succeeded.");

        FulfillmentCompensationFailureTotal = Meter.CreateCounter<long>(
            MetricNames.FulfillmentCompensationFailureTotal,
            unit: "1",
            description: "Compensation attempts that failed.");

        // Notification
        NotificationDuplicateSuppressedTotal = Meter.CreateCounter<long>(
            MetricNames.NotificationDuplicateSuppressedTotal,
            unit: "1",
            description: "Duplicate notifications suppressed.");

        NotificationProviderAcceptedTotal = Meter.CreateCounter<long>(
            MetricNames.NotificationProviderAcceptedTotal,
            unit: "1",
            description: "Notifications accepted by the downstream provider.");

        NotificationDeliveredTotal = Meter.CreateCounter<long>(
            MetricNames.NotificationDeliveredTotal,
            unit: "1",
            description: "Notifications confirmed delivered to the recipient.");

        NotificationPendingStatusTotal = Meter.CreateCounter<long>(
            MetricNames.NotificationPendingStatusTotal,
            unit: "1",
            description: "Notifications in a pending-status state.");

        NotificationInvalidRecipientTotal = Meter.CreateCounter<long>(
            MetricNames.NotificationInvalidRecipientTotal,
            unit: "1",
            description: "Notifications rejected due to a permanent invalid-recipient error.");

        // Telemetry self-monitoring
        TelemetryUncorrelatedRecordTotal = Meter.CreateCounter<long>(
            MetricNames.TelemetryUncorrelatedRecordTotal,
            unit: "1",
            description: "Records processed that lacked a non-null correlation_id.");

        TelemetryExportDroppedTotal = Meter.CreateCounter<long>(
            MetricNames.TelemetryExportDroppedTotal,
            unit: "1",
            description: "Telemetry data points dropped during export.");

        // Alert signals (spec 13 §6 / §25.4)
        AcceptanceIdempotencyConflictTotal = Meter.CreateCounter<long>(
            MetricNames.AcceptanceIdempotencyConflictTotal,
            unit: "1",
            description: "Idempotency key reused with a different payload (Inv-3).");

        FulfillmentCapacityInvariantViolationTotal = Meter.CreateCounter<long>(
            MetricNames.FulfillmentCapacityInvariantViolationTotal,
            unit: "1",
            description: "First-party capacity overcommit / negative-capacity violation (Inv-6).");

        SecurityTenantCrossScopeAttemptTotal = Meter.CreateCounter<long>(
            MetricNames.SecurityTenantCrossScopeAttemptTotal,
            unit: "1",
            description: "Access attempted for an operation belonging to another tenant (Inv-13).");

        NotificationMissingTerminalTotal = Meter.CreateCounter<long>(
            MetricNames.NotificationMissingTerminalTotal,
            unit: "1",
            description: "Terminal operation lacked a notification past threshold (Inv-8/Inv-9).");

        // Observable gauges (fulfillment.dlq.* and fulfillment.manual_review.*)
        // Callbacks read the cached fields — no DB I/O in the gauge callback.
        Meter.CreateObservableGauge(
            MetricNames.FulfillmentDlqDepth,
            () => DlqDepth,
            unit: "1",
            description: "Dead-letter queue depth (quarantined items).");

        Meter.CreateObservableGauge(
            MetricNames.FulfillmentDlqOldestAgeSeconds,
            () => DlqOldestAgeSeconds,
            unit: "s",
            description: "Age of the oldest quarantined dead-letter item in seconds.");

        Meter.CreateObservableGauge(
            MetricNames.FulfillmentManualReviewDepth,
            () => ManualReviewDepth,
            unit: "1",
            description: "Manual-review work-item queue depth.");

        Meter.CreateObservableGauge(
            MetricNames.FulfillmentManualReviewOldestAgeSeconds,
            () => ManualReviewOldestAgeSeconds,
            unit: "s",
            description: "Age of the oldest open manual-review work item in seconds.");
    }

    /// <inheritdoc />
    public void Dispose() => Meter.Dispose();
}
