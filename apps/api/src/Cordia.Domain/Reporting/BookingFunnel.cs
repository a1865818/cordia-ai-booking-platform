namespace Cordia.Domain.Reporting;

/// <summary>
/// Separated booking funnel counts for a tenant over a time window (spec 13 §3, §5.3, §26).
///
/// The three primary funnels — Accepted, Confirmed, Notified — MUST NEVER be collapsed into
/// a single number (Inv-16, §26).  Each represents a distinct, authoritative state transition:
///   Accepted  = every durable operation committed in the window (Inv-4).
///   Confirmed = those that reached Status=Booked (Inv-8 persisted).
///   Notified  = confirmed operations whose notification reached State=Delivered (Inv-9).
///
/// Additional sub-state counts for complete §5.3 lens coverage:
///   FailedTerminal = operations that terminated in a failure state (Rejected, FailedFinal,
///                    Expired, Waitlisted, AlternativesRequired-declined path — conservative set,
///                    only clearly terminal-failure values).
///   Ambiguous      = operations in Status=Reconciling (provider outcome unknown, §5.3).
///   NotifyPending  = Confirmed − Notified (Booked with no Delivered notification yet).
/// </summary>
public sealed record BookingFunnel(
    string TenantId,
    DateTimeOffset From,
    DateTimeOffset To,
    int Accepted,
    int Confirmed,
    int Notified,
    int FailedTerminal,
    int Ambiguous,
    int NotifyPending);
