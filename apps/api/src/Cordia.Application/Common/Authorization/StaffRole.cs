namespace Cordia.Application.Common.Authorization;

/// <summary>
/// The owner/operator staff-role taxonomy (spec 14 §24.5/§28.4; gate decision GD-1 2026-07-11 —
/// closes the §12/§31 "Owner and internal operations roles" open decision, coordinated with
/// spec 08 §12 item 2). Values are constrained to the ratified <c>actor_type</c> set:
/// <c>owner</c> (tenant proprietor — menu lifecycle and every operator action) and
/// <c>operator</c> (internal ops staff — replay/resume/resolve/preorder resolution,
/// transcript/model-trace access). Owner SATISFIES operator (owner ⊇ operator); an unknown or
/// blank role satisfies nothing (fail-closed).
///
/// The role assertion arrives as the <c>X-Actor-Role</c> header shim (GD-2) — the same trust
/// model as <c>X-Actor</c> until a staff-authentication spec exists; swapping the shim for a
/// claims-derived role changes controllers only, never this matrix.
/// </summary>
public static class StaffRole
{
    public const string Owner = "owner";
    public const string Operator = "operator";

    /// <summary>True iff the role value is one of the two ratified staff roles.</summary>
    public static bool IsKnown(string? role) => role is Owner or Operator;

    /// <summary>
    /// Core matrix decision: does <paramref name="providedRole"/> satisfy
    /// <paramref name="requiredRole"/>? Owner-required actions demand owner; operator-required
    /// actions accept operator or owner. Anything else — including unknown roles and unknown
    /// requirements — denies (fail-closed).
    /// </summary>
    public static bool Satisfies(string? providedRole, string requiredRole)
        => requiredRole switch
        {
            Owner => providedRole == Owner,
            Operator => providedRole is Operator or Owner,
            _ => false,
        };
}
