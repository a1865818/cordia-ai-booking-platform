namespace Cordia.Domain.Interfaces;

/// <summary>
/// Policy/validation seam for the <c>Validating→Rejected</c> edge (spec 06 §6.1, §11).
///
/// <para>Spec 04 owns the real policy engine; this is the spec-06 seam used to gate the
/// Validating step. Returns a rejection reason string when validation fails, or <c>null</c>
/// when the operation is valid and may proceed.</para>
/// </summary>
public interface IValidationPolicy
{
    /// <summary>
    /// Checks whether the booking operation satisfies all policy/validation rules.
    /// Returns a non-null rejection reason string when validation fails (→ <c>Validating→Rejected</c>);
    /// returns <c>null</c> when the operation is valid.
    /// </summary>
    Task<string?> CheckAsync(string tenantId, string operationId, CancellationToken ct = default);
}
