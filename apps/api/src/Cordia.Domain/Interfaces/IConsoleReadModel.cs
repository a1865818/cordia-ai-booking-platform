using Cordia.Domain.Console;
using Cordia.Domain.Enums;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Tenant-scoped, read-only queries backing the operations console's list screens.
///
/// One interface rather than six because they serve a single purpose — the console read
/// surface — and share the same source tables and canonical-payload reader; this mirrors
/// <see cref="IBookingFunnelReadModel"/>, which is likewise a read model per purpose
/// rather than per table.
///
/// Every method is a read: no state transition, no side effect, no new DDL. Each takes an
/// explicit <paramref name="tenantId"/> and a bounded <c>limit</c> so a console screen can
/// never issue an unbounded scan.
/// </summary>
public interface IConsoleReadModel
{
    /// <summary>
    /// Bookings whose start falls in [from, to), ordered by start time. The result reports
    /// whether the underlying scan was truncated — a booking's start time is not queryable
    /// in SQL, so the scan is bounded and partial results must be visible as such.
    /// </summary>
    Task<ScheduleResult> ListScheduleAsync(
        string tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Booking operations, newest first. When <paramref name="statuses"/> is non-empty only
    /// those statuses are returned; the values come from the ratified
    /// <see cref="OperationStatus"/> set (D1) — an unknown value is rejected by the validator.
    /// </summary>
    Task<IReadOnlyList<OperationSummary>> ListOperationsAsync(
        string tenantId,
        IReadOnlyCollection<OperationStatus> statuses,
        int limit,
        CancellationToken ct = default);

    /// <summary>Conversation sessions, most recently started first.</summary>
    Task<IReadOnlyList<ConversationSessionSummary>> ListSessionsAsync(
        string tenantId,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Waitlist enrollments with their latest offer state. When
    /// <paramref name="activeOnly"/> is true, cancelled enrollments are excluded.
    /// </summary>
    Task<IReadOnlyList<WaitlistEnrollmentSummary>> ListWaitlistEnrollmentsAsync(
        string tenantId,
        bool activeOnly,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Customers derived from booking history. <paramref name="query"/> filters on the
    /// opaque customer reference (case-insensitive contains); empty returns the most
    /// recently active.
    /// </summary>
    Task<CustomerListResult> ListCustomersAsync(
        string tenantId,
        string? query,
        int limit,
        CancellationToken ct = default);

    /// <summary>Per-day, per-channel and per-service aggregates over [from, to).</summary>
    Task<BookingOverview> GetOverviewAsync(
        string tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default);
}
