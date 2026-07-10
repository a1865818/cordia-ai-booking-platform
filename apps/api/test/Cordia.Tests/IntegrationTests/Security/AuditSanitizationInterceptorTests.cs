#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Security;

/// <summary>
/// Spec 14 / Inv-12 — the DbContext-level sanitization choke point: an audit
/// row written through ANY path (here the rawest one, db.AuditEvents.Add)
/// has PAN-shaped metadata masked at save time, while system-minted digit-run
/// identifiers survive byte-for-byte (control-plane precision).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class AuditSanitizationInterceptorTests(PostgresFixture fixture)
{
    [Fact]
    public async Task DirectAdd_WithPanInMetadata_IsMaskedAtSaveTime()
    {
        var tenantId = $"t14-auditsan-{Guid.NewGuid():N}";
        var eventId = $"evt_{Guid.NewGuid():N}";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.AuditEvents.Add(new AuditEvent
            {
                TenantId = tenantId,
                EventId = eventId,
                EventType = "test.sanitize",
                Actor = "system",
                SubjectRef = "subject-1",
                // A PAN leaked into free-text metadata by a hypothetical writer,
                // alongside a legit reference (no embedded Luhn window) that must survive.
                Metadata = """{"note":"customer said 4111 1111 1111 1111","reference":"5260181590830166"}""",
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var row = await readCtx.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.TenantId == tenantId && e.EventId == eventId);

        Assert.DoesNotContain("4111 1111 1111 1111", row.Metadata);
        Assert.Contains("[REDACTED]", row.Metadata);
        Assert.Contains("5260181590830166", row.Metadata); // reference with no Luhn window intact
    }
}
