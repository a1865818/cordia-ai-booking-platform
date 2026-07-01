namespace Cordia.Domain.Waitlist;

/// <summary>
/// Recorded customer consent to auto-book a waitlist match without an offer round-trip
/// (spec 11 §4.4, §20). Present ONLY when the customer gave explicit, evidenced consent
/// (<see cref="ConsentEvidenceId"/>). A match is auto-booked under this grant only when the
/// matched slot satisfies <em>every</em> recorded constraint; otherwise an offer is always
/// sent (silent auto-book is prohibited by default — §20).
///
/// <para>This is the typed projection of the <c>waitlist_enrollments.auto_book</c> jsonb column;
/// see <see cref="WaitlistEnrollmentExtensions"/>.</para>
/// </summary>
public sealed record AutoBookGrant(
    string ConsentEvidenceId,
    DateTimeOffset ConsentedAt,
    int MaxPartySize,
    DateTimeOffset ConstraintFrom,
    DateTimeOffset ConstraintTo,
    long MaxDepositAmountMinor,
    string DepositCurrency)
{
    /// <summary>
    /// Returns <c>true</c> when a matched slot (<paramref name="partySize"/> at
    /// <paramref name="slotTime"/>, optionally requiring <paramref name="requiredDeposit"/>)
    /// falls entirely within this grant's recorded constraints, so the platform may auto-book
    /// it without an offer (§20). Any breach — party too large, slot outside the consented
    /// window, deposit currency mismatch, or deposit above the consented ceiling — returns
    /// <c>false</c>, and the caller must fall back to an offer.
    /// </summary>
    public bool Permits(int partySize, DateTimeOffset slotTime, DepositTerms? requiredDeposit)
        => partySize <= MaxPartySize
        && slotTime >= ConstraintFrom
        && slotTime <= ConstraintTo
        && (requiredDeposit is null
            || (string.Equals(requiredDeposit.Currency, DepositCurrency, StringComparison.Ordinal)
                && requiredDeposit.AmountMinor <= MaxDepositAmountMinor));
}
