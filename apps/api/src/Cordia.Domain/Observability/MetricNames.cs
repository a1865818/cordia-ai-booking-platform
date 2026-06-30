namespace Cordia.Domain.Observability;

/// <summary>
/// Frozen metric-name registry (§4.2). String values are the contract — other specs emit against
/// these names. Change a value here and the metric vanishes in the backend.
/// </summary>
public static class MetricNames
{
    /// <summary>OpenTelemetry Meter name for the Cordia service.</summary>
    public const string MeterName = "Cordia";

    /// <summary>OpenTelemetry ActivitySource name for the Cordia service.</summary>
    public const string ActivitySourceName = "Cordia";

    // -------------------------------------------------------------------------
    // Agent metrics (foreground — emitted by specs 01, 02, 03)
    // -------------------------------------------------------------------------

    /// <summary>Time to first response per agent turn (histogram, ms).</summary>
    public const string AgentTurnTimeToFirstResponseMs = "agent.turn.time_to_first_response_ms";

    /// <summary>Per-turn end-to-end latency (histogram, ms).</summary>
    public const string AgentTurnLatencyMs = "agent.turn.latency_ms";

    /// <summary>Barge-in events that successfully interrupted the agent (counter).</summary>
    public const string AgentBargeinSuccessTotal = "agent.bargein.success_total";

    /// <summary>Total barge-in attempts (counter).</summary>
    public const string AgentBargeinAttemptTotal = "agent.bargein.attempt_total";

    /// <summary>Session reconnects (counter).</summary>
    public const string AgentSessionReconnectTotal = "agent.session.reconnect_total";

    /// <summary>STT/ASR confidence score by field type (histogram).</summary>
    public const string AgentSttConfidence = "agent.stt.confidence";

    /// <summary>Clarification prompts issued, labelled by reason (counter).</summary>
    public const string AgentClarificationTotal = "agent.clarification_total";

    /// <summary>Spell-back prompts issued (counter).</summary>
    public const string AgentSpellbackTotal = "agent.spellback_total";

    /// <summary>Draft validation failures, labelled by field (counter).</summary>
    public const string AgentDraftValidationFailureTotal = "agent.draft.validation_failure_total";

    /// <summary>Confirmation flow abandonments (counter).</summary>
    public const string AgentConfirmationAbandonmentTotal = "agent.confirmation.abandonment_total";

    /// <summary>Tool errors, labelled by tool and category (counter).</summary>
    public const string AgentToolErrorTotal = "agent.tool.error_total";

    /// <summary>Tool timeouts, labelled by tool (counter).</summary>
    public const string AgentToolTimeoutTotal = "agent.tool.timeout_total";

    /// <summary>Human-handoff events, labelled by reason (counter).</summary>
    public const string AgentHandoffTotal = "agent.handoff_total";

    // -------------------------------------------------------------------------
    // Fulfillment metrics (background — emitted by specs 04, 05, 06, 07, 09)
    // -------------------------------------------------------------------------

    /// <summary>Accepted-to-held latency (histogram, ms).</summary>
    public const string FulfillmentAcceptedToHeldMs = "fulfillment.accepted_to_held_ms";

    /// <summary>Accepted-to-booked latency by provider (histogram, ms).</summary>
    public const string FulfillmentAcceptedToBookedMs = "fulfillment.accepted_to_booked_ms";

    /// <summary>Capacity conditional-write race losses (counter).</summary>
    public const string FulfillmentCapacityRaceLossTotal = "fulfillment.capacity.race_loss_total";

    /// <summary>Capacity holds successfully acquired (counter).</summary>
    public const string FulfillmentHoldAcquiredTotal = "fulfillment.hold.acquired_total";

    /// <summary>Capacity holds that expired before finalisation (counter).</summary>
    public const string FulfillmentHoldExpiredTotal = "fulfillment.hold.expired_total";

    /// <summary>Operations reaching a terminal state, labelled by state (counter).</summary>
    public const string FulfillmentOperationTerminalTotal = "fulfillment.operation.terminal_total";

    /// <summary>Retry attempts, labelled by category (counter).</summary>
    public const string FulfillmentRetryAttemptTotal = "fulfillment.retry.attempt_total";

    /// <summary>Elapsed time across retries for a single operation (histogram, ms).</summary>
    public const string FulfillmentRetryElapsedMs = "fulfillment.retry.elapsed_ms";

    /// <summary>Operations with an ambiguous provider outcome (counter).</summary>
    public const string FulfillmentAmbiguousOutcomeTotal = "fulfillment.ambiguous_outcome_total";

    /// <summary>Reconciliation events, labelled by result (counter).</summary>
    public const string FulfillmentReconciliationTotal = "fulfillment.reconciliation_total";

    /// <summary>Duplicate message deliveries detected (counter).</summary>
    public const string FulfillmentDeliveryDuplicateTotal = "fulfillment.delivery.duplicate_total";

    /// <summary>Inbox records suppressed by deduplication (counter).</summary>
    public const string FulfillmentInboxDedupSuppressedTotal = "fulfillment.inbox.dedup_suppressed_total";

    /// <summary>Provider idempotency-key conflicts, labelled by provider (counter).</summary>
    public const string FulfillmentProviderIdempotencyConflictTotal = "fulfillment.provider.idempotency_conflict_total";

    /// <summary>Dead-letter queue depth (gauge).</summary>
    public const string FulfillmentDlqDepth = "fulfillment.dlq.depth";

    /// <summary>Age of the oldest item in the dead-letter queue, in seconds (gauge).</summary>
    public const string FulfillmentDlqOldestAgeSeconds = "fulfillment.dlq.oldest_age_seconds";

    /// <summary>Manual-review work-item queue depth (gauge).</summary>
    public const string FulfillmentManualReviewDepth = "fulfillment.manual_review.depth";

    /// <summary>Age of the oldest manual-review work item, in seconds (gauge).</summary>
    public const string FulfillmentManualReviewOldestAgeSeconds = "fulfillment.manual_review.oldest_age_seconds";

    /// <summary>Compensation attempts that succeeded (counter).</summary>
    public const string FulfillmentCompensationSuccessTotal = "fulfillment.compensation.success_total";

    /// <summary>Compensation attempts that failed (counter).</summary>
    public const string FulfillmentCompensationFailureTotal = "fulfillment.compensation.failure_total";

    // -------------------------------------------------------------------------
    // Notification metrics (emitted by spec 10)
    // -------------------------------------------------------------------------

    /// <summary>Duplicate notifications suppressed (counter).</summary>
    public const string NotificationDuplicateSuppressedTotal = "notification.duplicate_suppressed_total";

    /// <summary>Notifications accepted by the downstream provider (counter).</summary>
    public const string NotificationProviderAcceptedTotal = "notification.provider_accepted_total";

    /// <summary>Notifications confirmed delivered to the recipient (counter).</summary>
    public const string NotificationDeliveredTotal = "notification.delivered_total";

    /// <summary>Notifications in a pending-status state (counter).</summary>
    public const string NotificationPendingStatusTotal = "notification.pending_status_total";

    /// <summary>Notifications rejected due to a permanent invalid-recipient error (counter).</summary>
    public const string NotificationInvalidRecipientTotal = "notification.invalid_recipient_total";

    // -------------------------------------------------------------------------
    // Telemetry self-monitoring (§8)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Records processed that lacked a non-null correlation_id (counter).
    /// Emitted so gaps are visible rather than silently swallowed (§8).
    /// </summary>
    public const string TelemetryUncorrelatedRecordTotal = "telemetry.uncorrelated_record_total";

    /// <summary>
    /// Telemetry data points dropped during export (counter).
    /// Indicates sustained export loss when rising (§8 fail-open).
    /// </summary>
    public const string TelemetryExportDroppedTotal = "telemetry.export.dropped_total";

    // -------------------------------------------------------------------------
    // Alert signals (spec 13 §6 / §25.4 — counters the §25.4 alerts fire on)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Idempotency key reused with a DIFFERENT payload (counter).
    /// Inv-3 breach signal — fires the idempotency-key-reuse-with-different-payload alert (§25.4).
    /// </summary>
    public const string AcceptanceIdempotencyConflictTotal = "acceptance.idempotency_conflict_total";

    /// <summary>
    /// First-party capacity overcommit / negative-capacity invariant violation detected (counter).
    /// Inv-6 guardrail — fires the capacity-invariant-violation alert (§25.4, §27.3).
    /// </summary>
    public const string FulfillmentCapacityInvariantViolationTotal = "fulfillment.capacity.invariant_violation_total";

    /// <summary>
    /// Access attempted for an operation belonging to another tenant (counter).
    /// Inv-13 enforcement signal — fires the tenant cross-scope access-attempt alert (§25.4).
    /// </summary>
    public const string SecurityTenantCrossScopeAttemptTotal = "security.tenant_cross_scope_attempt_total";

    /// <summary>
    /// Terminal booking operation lacked an accepted notification past threshold (counter).
    /// Inv-8/Inv-9 watchdog — fires the missing-notification-for-terminal-outcome alert (§25.4).
    /// </summary>
    public const string NotificationMissingTerminalTotal = "notification.missing_terminal_total";

    // -------------------------------------------------------------------------
    // Tag (label) key constants
    // -------------------------------------------------------------------------

    /// <summary>Label key constants for metric tag sets.</summary>
    public static class Tags
    {
        /// <summary>Booking/notification provider identifier.</summary>
        public const string Provider = "provider";

        /// <summary>Entity or operation state.</summary>
        public const string State = "state";

        /// <summary>Retry or error category classification.</summary>
        public const string Category = "category";

        /// <summary>Outcome of an operation or reconciliation attempt.</summary>
        public const string Result = "result";

        /// <summary>Agent tool name.</summary>
        public const string Tool = "tool";

        /// <summary>Intent or booking field name.</summary>
        public const string Field = "field";

        /// <summary>STT/ASR field type (e.g. date, name, number).</summary>
        public const string FieldType = "field_type";

        /// <summary>Reason for clarification, handoff, or other labelled event.</summary>
        public const string Reason = "reason";
    }
}
