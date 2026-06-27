namespace Cordia.Domain.Interfaces;

/// <summary>Runs a unit of work inside ONE database transaction (spec 04 §10.3, Inv-4).</summary>
public interface IUnitOfWork
{
    /// <summary>Begin a transaction, run <paramref name="work"/>, then commit. If <paramref name="work"/>
    /// throws, the transaction is rolled back and the exception propagates (so callers can inspect it,
    /// e.g. to detect a unique-constraint election loss). No partial state is durably persisted.</summary>
    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken ct = default);
}
