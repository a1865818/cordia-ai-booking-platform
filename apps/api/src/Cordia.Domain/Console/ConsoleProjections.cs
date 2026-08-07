using Cordia.Domain.Enums;
using Cordia.Domain.Generated;

// Both namespaces declare a TenantChannel; the persisted conversation_sessions column uses
// the Domain pg-enum, so bind the simple name to it explicitly.
using TenantChannel = Cordia.Domain.Enums.TenantChannel;

namespace Cordia.Domain.Console;

/// <summary>
/// Read-only projections backing the operations console's list screens.
///
/// These are **derived views over existing tables** — no new table, column or pg-enum is
/// introduced (spec 12 owns all DDL). In particular there is no customers table:
/// <see cref="CustomerSummary"/> is an aggregate over booking operations and their
/// confirmed intent revisions.
///
/// Every projection is tenant-scoped by its query; none carries card data (Inv-12).
/// </summary>
public sealed record ScheduledBooking(
    /// <summary>
    /// Null while the operation is still in flight — there is no booking row to act on yet.
    /// Callers must not fabricate one: cancel/modify against an operation id would 404.
    /// </summary>
    string? BookingId,
    string OperationId,
    string? BookingReference,
    DateTimeOffset? StartsAt,
    int? PartySize,
    int? DurationMinutes,
    string? ServiceId,
    string? CustomerRef,
    /// <summary>Null while the operation is in flight — there is no booking to have a lifecycle.</summary>
    string? LifecycleStatus,
    OperationStatus OperationStatus,
    OperationTypeEnum OperationType,
    DateTimeOffset UpdatedAt);

/// <summary>
/// A page of scheduled bookings. <paramref name="Truncated"/> is true when the scan hit its
/// ceiling with rows still unexamined, so the caller can say the list is partial instead of
/// presenting an incomplete day as complete.
/// </summary>
public sealed record ScheduleResult(IReadOnlyList<ScheduledBooking> Bookings, bool Truncated);

/// <summary>Derived customers plus whether the scan was truncated (see <see cref="ScheduleResult"/>).</summary>
public sealed record CustomerListResult(IReadOnlyList<CustomerSummary> Customers, bool Truncated);

/// <summary>A booking operation as the Queue/Operations board needs it.</summary>
public sealed record OperationSummary(
    string OperationId,
    string? BookingId,
    string IntentId,
    int IntentRevision,
    OperationTypeEnum OperationType,
    OperationStatus Status,
    string? StatusReason,
    bool IsPreorder,
    string? CorrelationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// An open manual-recovery work item as the operator queue needs it (spec 06 §17.5, FM#11).
///
/// <para><paramref name="OperationStatus"/> is the operation's CURRENT status, resolved at read
/// time rather than stored, and null when the linked operation is not visible to this tenant. A work
/// item stays open until a person closes it, so the operation underneath can have moved — or
/// finished — since it was raised, and an operator needs the live answer to know whether the item
/// is still real work.</para>
/// </summary>
public sealed record OperationWorkItemSummary(
    string WorkItemId,
    string OperationId,
    string Reason,
    DateTimeOffset SlaDeadline,
    string? Assignee,
    DateTimeOffset CreatedAt,
    OperationStatus? OperationStatus);

/// <summary>A conversation session as the Conversations thread list needs it.</summary>
public sealed record ConversationSessionSummary(
    string SessionId,
    TenantChannel Channel,
    string? CustomerRef,
    ConsentState? ConsentState,
    bool? RecordingAllowed,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);

/// <summary>A waitlist enrollment plus the state of its most recent offer, if any.</summary>
public sealed record WaitlistEnrollmentSummary(
    string EnrollmentId,
    string CustomerRef,
    string ServiceId,
    string LocationId,
    int PartySize,
    DateTimeOffset AcceptableFrom,
    DateTimeOffset AcceptableTo,
    bool AutoBookAuthorized,
    bool Cancelled,
    DateTimeOffset CreatedAt,
    /// <summary>
    /// Identifies the offer for <c>accept</c>/<c>decline</c>. Without it a caller can see that an
    /// offer exists but has nothing to address, which is why the console could display offer
    /// state but never act on it. Null when the enrollment has no offer yet.
    /// </summary>
    string? LatestOfferId,
    WaitlistOfferStatus? LatestOfferStatus,
    DateTimeOffset? LatestOfferExpiresAt);

/// <summary>
/// A customer as seen from their booking history. Derived, not stored — the identity is
/// the opaque <c>customer_ref</c> carried on confirmed intent revisions, never a name or
/// contact detail (spec 14 minimisation).
/// </summary>
public sealed record CustomerSummary(
    string CustomerRef,
    int BookingCount,
    DateTimeOffset? FirstSeenAt,
    DateTimeOffset? LastSeenAt,
    string? LastServiceId,
    string? LastBookingId);

/// <summary>One day's counts for the analytics chart.</summary>
public sealed record OverviewDay(DateOnly Date, int Accepted, int Booked);

/// <summary>One channel's share of accepted operations.</summary>
public sealed record OverviewChannel(TenantChannel Channel, int Count);

/// <summary>One service's share of accepted operations.</summary>
public sealed record OverviewService(string ServiceId, int Count);

/// <summary>
/// Aggregates for the Analytics screen over [from, to).
///
/// Deliberately does NOT include an "agent vs human" split: nothing in the data model
/// records who handled a booking, and inventing a number would violate Inv-16.
/// </summary>
public sealed record BookingOverview(
    string TenantId,
    DateTimeOffset From,
    DateTimeOffset To,
    /// <summary>True when the channel/service breakdown was capped; per-day counts are always exact.</summary>
    bool BreakdownTruncated,
    IReadOnlyList<OverviewDay> PerDay,
    IReadOnlyList<OverviewChannel> Channels,
    IReadOnlyList<OverviewService> TopServices);
