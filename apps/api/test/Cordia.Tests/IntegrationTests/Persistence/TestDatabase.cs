namespace Cordia.Tests.IntegrationTests.Persistence;

/// <summary>
/// The one place the integration suite's Postgres connection strings are written down.
///
/// <para><b>Why this exists.</b> These literals were duplicated seven times — the maintenance
/// string in all three fixtures plus <see cref="TestSchemaLock"/>, and the test string in all
/// three fixtures. Every copy was byte-identical, which is exactly the state that drifts silently:
/// nothing fails when one copy changes port or password, the fixtures simply start talking to
/// different databases and the failure surfaces somewhere unrelated.</para>
///
/// <para>The host, port and credentials must match what <c>docker-compose.yml</c> provisions for
/// the <c>cordia-postgres</c> service, and CI reuses that same compose file rather than declaring
/// its own Postgres, so compose is the upstream source of truth for everything but the database
/// names.</para>
/// </summary>
internal static class TestDatabase
{
    /// <summary>
    /// Postgres's always-present maintenance database. Used to <c>CREATE DATABASE</c> the test
    /// database (which may not exist yet) and to hold the advisory lock in
    /// <see cref="TestSchemaLock"/> — the lock cannot live in the database it guards the creation of.
    /// </summary>
    public const string MaintenanceConnStr =
        "Host=localhost;Port=5432;Database=postgres;Username=cordia;Password=localdev";

    /// <summary>
    /// The shared integration-test database. One database backs every fixture family, which is
    /// why the claim-scoping helpers in <c>IntegrationTests/Messaging</c> exist.
    /// </summary>
    public const string ConnStr =
        "Host=localhost;Port=5432;Database=Cordia_API_Test;Username=cordia;Password=localdev";
}
