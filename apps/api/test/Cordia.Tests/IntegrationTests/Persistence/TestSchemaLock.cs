using Npgsql;

namespace Cordia.Tests.IntegrationTests.Persistence;

/// <summary>
/// Serialises test-database creation and migration across every fixture that performs it.
///
/// <para><b>The race this closes.</b> Three fixtures bring the schema up, and xUnit starts their
/// collections in parallel: <see cref="PostgresFixture"/> calls <c>MigrateAsync()</c> directly, while
/// <c>AcceptanceApiFixture</c> and <c>TenantResolutionApiFixture</c> each boot an Alba host with
/// <c>DatabaseBootstrap:MigrateOnStartup=true</c>, which makes <c>Program.cs</c> migrate. Against an
/// <b>empty</b> database all three then apply the same 39 migrations at once, and the losers die with
/// <c>42P07: relation "tenant_resources" already exists</c> — taking the whole collection with them,
/// because the failure happens inside <c>InitializeAsync</c> of a collection fixture.</para>
///
/// <para><b>Why it has stayed hidden.</b> It is a FIRST-RUN race. Once any run has created the
/// schema, every later run finds it already there and the race cannot fire, so a developer who runs
/// the suite twice sees it pass and concludes it was a flake. CI is the opposite case: a fresh
/// database on every run means it would fire on <i>every</i> run. Measured on this branch — a run
/// against an empty database failed 15 tests across the two Alba collections; the next run against
/// the now-existing schema passed all of them.</para>
///
/// <para><b>Why an advisory lock rather than a static gate.</b> A <c>static SemaphoreSlim</c> would
/// serialise the three fixtures inside one test process, which is enough for the case above. It
/// would do nothing about two <c>dotnet test</c> processes running at once — the documented trap in
/// this repo where concurrent runs silently truncate totals and diverge binaries. A PostgreSQL
/// advisory lock is held in the database, so it serialises across processes as well.</para>
///
/// <para>The lock is taken on the <b>maintenance</b> database (<c>postgres</c>), not the test
/// database, because the first caller may still have to <c>CREATE DATABASE</c> — the target may not
/// exist yet. Both steps run under the one lock, so create-then-migrate is atomic with respect to
/// every other fixture.</para>
///
/// <para>Session-level (<c>pg_advisory_lock</c>), not transaction-level: the lock must outlive the
/// migration's own transactions, so it is bound to a dedicated connection that is held open until
/// the returned handle is disposed.</para>
/// </summary>
internal static class TestSchemaLock
{
    private const string MaintenanceConnStr =
        "Host=localhost;Port=5432;Database=postgres;Username=cordia;Password=localdev";

    /// <summary>
    /// Arbitrary but fixed. Advisory-lock keys share one namespace per database, so this only has to
    /// be distinct from any other advisory lock taken against the maintenance database — nothing
    /// else in this repo takes one.
    /// </summary>
    private const long SchemaLockKey = 20260826_01L;

    /// <summary>
    /// Blocks until no other fixture (in this process or any other) is creating or migrating the
    /// test database. Dispose the returned handle to release.
    /// </summary>
    public static async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct = default)
    {
        var conn = new NpgsqlConnection(MaintenanceConnStr);
        try
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pg_advisory_lock($1)";
            cmd.Parameters.AddWithValue(SchemaLockKey);
            await cmd.ExecuteNonQueryAsync(ct);
            return new Handle(conn, SchemaLockKey);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private sealed class Handle(NpgsqlConnection conn, long key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT pg_advisory_unlock($1)";
                cmd.Parameters.AddWithValue(key);
                await cmd.ExecuteNonQueryAsync();
            }
            catch
            {
                // Closing the connection releases a session-level advisory lock anyway, so an
                // explicit unlock failing is not worth failing a test run over.
            }
            finally
            {
                await conn.DisposeAsync();
            }
        }
    }
}
