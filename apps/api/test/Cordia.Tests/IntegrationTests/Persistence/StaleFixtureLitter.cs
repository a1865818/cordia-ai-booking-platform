using Npgsql;

namespace Cordia.Tests.IntegrationTests.Persistence;

/// <summary>
/// Settles rows left behind by PREVIOUS test runs, once per session, before any test executes.
///
/// <para><b>Why this is necessary and not merely tidy.</b> The integration suite shares one
/// database across runs and isolates tests by tenant id, which works because almost every query is
/// tenant-scoped. The exception is a recovery sweeper: those are deliberately CROSS-TENANT and read
/// a BOUNDED batch — <c>FindStuckAsync</c> takes 500 stuck sagas, the SLA evaluator takes 50
/// non-terminal operations, the hold sweeper takes a page of expired holds — ordered oldest-first.
/// </para>
///
/// <para>Every run leaves its non-terminal fixtures behind, so that backlog grows without limit.
/// Once it exceeds a sweeper's batch size, a test's own freshly-seeded row falls outside the window
/// and the test fails — reporting a broken sweeper when the sweeper is fine and the box simply has
/// too much history on it. That is a false failure that gets worse with every run and cannot be
/// reproduced on a clean machine, which is the worst shape a test failure can take. It had already
/// happened: 529 stuck sagas against a batch of 500.</para>
///
/// <para><b>Settled, not deleted.</b> These rows are referenced by others (operations, holds,
/// intents), so removing them would either cascade or fail on a foreign key. Moving them to a
/// terminal state makes them invisible to the sweepers — which is the entire problem — while
/// leaving the graph intact.</para>
///
/// <para><b>Why the age threshold is safe.</b> A day is far longer than any suite run, so nothing
/// this touches can belong to a test in flight — including tests in the other collection, which
/// runs in parallel and seeds rows stamped "now". The one test that deliberately ages a row past
/// this threshold creates it DURING the test, long after this has run.</para>
/// </summary>
internal static class StaleFixtureLitter
{
    /// <summary>Runs at most once per test process, however many fixtures ask for it.</summary>
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static bool _done;

    internal static async Task SettleAsync(string connectionString)
    {
        if (_done)
            return;

        await _gate.WaitAsync();
        try
        {
            if (_done)
                return;

            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE preorder_sagas
                   SET saga_state = 'completed'
                 WHERE updated_at < now() - interval '1 day'
                   AND saga_state NOT IN ('completed', 'failed_final', 'manual_review');

                UPDATE capacity_holds
                   SET status = 'expired'
                 WHERE expires_at < now() - interval '1 day'
                   AND status = 'active';

                UPDATE booking_operations
                   SET status = 'failed_final'
                 WHERE updated_at < now() - interval '1 day'
                   AND status IN ('accepted', 'validating', 'reserving_capacity', 'held',
                                  'awaiting_payment', 'finalizing', 'reconciling',
                                  'compensation_required', 'manual_review');
                """;

            await cmd.ExecuteNonQueryAsync();
            _done = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
