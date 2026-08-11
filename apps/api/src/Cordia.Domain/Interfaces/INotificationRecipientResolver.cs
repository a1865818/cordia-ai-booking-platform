using Cordia.Domain.Notifications;

namespace Cordia.Domain.Interfaces;

/// <summary>Resolves the validated recipient for a booking operation. Returns <c>null</c> when no valid destination exists.</summary>
public interface INotificationRecipientResolver
{
    Task<ResolvedRecipient?> ResolveAsync(
        string tenantId,
        string operationId,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves the destination for a notification that has NO booking operation to resolve from —
    /// a spec-11 waitlist offer/expiry, whose <c>op_wl_*</c> id is deliberately not persisted on
    /// the row (spec 10 ratifies <c>OperationId</c> as "null for non-operation messages", because a
    /// non-null value would violate the composite FK before the offer is accepted).
    ///
    /// <para><b>Why recovery needs this.</b> The send path resolves through
    /// <see cref="ResolveAsync"/> using the in-flight <c>op_wl_*</c> id, but that id is gone by the
    /// time an ambiguous send is reconciled or retried — the row keeps only the enrollment id, on
    /// <c>correlation_id</c>. Without an enrollment-keyed entry point those lanes had nothing to
    /// resolve from and settled every waitlist offer <c>InvalidRecipient</c>, which is terminal:
    /// a guest who WAS notified was permanently recorded as unreachable.</para>
    ///
    /// <para><b>Keyed on the enrollment, not the offer.</b> One enrollment can have many offers, so
    /// enrollment → offer is ambiguous; the address lives on the enrollment either way, which is
    /// all the offer lookup was ever used to reach.</para>
    ///
    /// <para>The returned <see cref="ResolvedRecipient.RecipientRef"/> is enrollment-derived and is
    /// NOT the row's persisted ref. Callers on this path must keep using the ref already stored on
    /// the notification: it feeds <c>notification_id</c>, and re-deriving it would break dedup and
    /// notify the guest twice (Inv-9).</para>
    /// </summary>
    Task<ResolvedRecipient?> ResolveByEnrollmentAsync(
        string tenantId,
        string enrollmentId,
        CancellationToken ct = default);
}
