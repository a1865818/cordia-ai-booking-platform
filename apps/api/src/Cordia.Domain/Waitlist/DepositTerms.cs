namespace Cordia.Domain.Waitlist;

/// <summary>
/// The deposit a waitlist enrollment requires when a match converts to a booking
/// (spec 11 §4.4). Null on the enrollment ⇒ no deposit required. Checked against an
/// <see cref="AutoBookGrant"/>'s ceiling before any auto-book (§20).
///
/// <para>This is the typed projection of the <c>waitlist_enrollments.deposit_terms</c> jsonb
/// column; see <see cref="WaitlistEnrollmentExtensions"/>. The deposit is collected via the
/// spec-09 deposit seam at accept time, not here.</para>
/// </summary>
public sealed record DepositTerms(
    long AmountMinor,
    string Currency);
