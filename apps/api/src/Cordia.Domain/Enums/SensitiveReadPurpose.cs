using System.Collections.Frozen;

namespace Cordia.Domain.Enums;

/// <summary>
/// Ratified purposes for an operator's **sensitive read** (spec 14 §2 "role/purpose-restricted
/// transcript access", §3.1 step 4, §24.5).
///
/// Why a closed set, when <c>reason_code</c> is <c>str</c> in the contract: spec 14 requires the
/// reason to be *recorded*, and the write path already did that faithfully. What it could not do is
/// make the record **mean** anything — a client is free to send one constant for every read, and the
/// console did exactly that (<c>"operator_review"</c> on every transcript open). An audit trail
/// where every row says the same thing cannot distinguish a dispute investigation from idle
/// browsing, which is the property §2 asks for. Ratifying the vocabulary server-side is what makes
/// the recorded purpose evidence rather than decoration.
///
/// Deliberately scoped to sensitive **reads**. It is NOT applied to
/// <c>IOperatorActionRequest.ReasonCode</c> (DLQ replay, catalog review, manual recovery): those
/// carry open-ended operational narrative like <c>provider_outage_recovered</c>, where a fixed
/// vocabulary would lose information rather than add it. Nor to the booking <c>reason_code</c>
/// business field (<c>customer_request</c>, <c>complaint</c>) — a different vocabulary saying why
/// the *guest* changed a booking, not why an *operator* looked at data.
///
/// Authoritative for <c>apps/api</c> only, and intentionally NOT a generated contract: spec 14 owns
/// <c>AuditEventEnvelope.reason_code</c> and types it <c>str | None</c>. Ratifying it in the Pydantic
/// contract would bind all three lanes and is a spec-14 decision, not this one's.
/// </summary>
public static class SensitiveReadPurpose
{
    /// <summary>Investigating a disputed charge, booking, or guest claim.</summary>
    public const string DisputeReview = "dispute_review";

    /// <summary>Following up a complaint the guest raised about this interaction.</summary>
    public const string GuestComplaint = "guest_complaint";

    /// <summary>The guest asked for a record of, or clarification about, their own conversation.</summary>
    public const string GuestRequest = "guest_request";

    /// <summary>Sampling for service quality — the routine, lowest-privilege reason.</summary>
    public const string QualityReview = "quality_review";

    /// <summary>Reviewing a specific interaction to coach staff or tune the agent.</summary>
    public const string TrainingReview = "training_review";

    /// <summary>Security or fraud investigation on this tenant's account.</summary>
    public const string SecurityInvestigation = "security_investigation";

    /// <summary>Preservation or disclosure compelled by a legal hold or lawful request.</summary>
    public const string LegalHold = "legal_hold";

    /// <summary>Every ratified purpose, in declaration order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        DisputeReview,
        GuestComplaint,
        GuestRequest,
        QualityReview,
        TrainingReview,
        SecurityInvestigation,
        LegalHold,
    ];

    private static readonly FrozenSet<string> Ratified =
        All.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// True when <paramref name="purpose"/> is one of the ratified values.
    ///
    /// Case-sensitive on purpose: the audit column is compared and grouped as an exact string, so
    /// admitting <c>Dispute_Review</c> alongside <c>dispute_review</c> would silently split one
    /// purpose into two rows in every report built on it.
    /// </summary>
    public static bool IsRatified(string? purpose)
        => purpose is not null && Ratified.Contains(purpose);
}
