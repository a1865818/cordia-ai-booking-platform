namespace Cordia.Infrastructure.Retention;

/// <summary>Summary returned by <see cref="RetentionSweepHandler.SweepIdempotencyRecordsAsync"/>.</summary>
/// <param name="Pruned">Records physically removed and audit-logged.</param>
/// <param name="Skipped">Records skipped because of an open work/dispute window (Inv-14).</param>
public record RetentionSweepResult(int Pruned, int Skipped);
