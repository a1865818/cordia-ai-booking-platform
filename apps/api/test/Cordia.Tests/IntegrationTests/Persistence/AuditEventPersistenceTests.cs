#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Persistence;

/// <summary>
/// Verifies append-only semantics and jsonb round-trip for audit_events.
/// Append-only is enforced by the insert-only <see cref="AuditEventRepository"/> interface:
/// no Update or Delete methods exist.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class AuditEventPersistenceTests(PostgresFixture fixture)
{
    // (1) Insert audit event with Metadata jsonb; GetAsync round-trips metadata.
    [Fact]
    public async Task Insert_And_GetAsync_RoundTripsMetadataJsonb()
    {
        var tenantId = $"t4-ae-{Guid.NewGuid():N}";
        var eventId = $"evt-{Guid.NewGuid():N}";
        const string metadata = """{"data_classes":["pii"]}""";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var repo = new AuditEventRepository(ctx);
            await repo.AddAsync(new AuditEvent
            {
                TenantId = tenantId,
                EventId = eventId,
                EventType = "booking.accepted",
                Actor = "system",
                SubjectRef = "op-abc",
                CorrelationId = "corr-1",
                Metadata = metadata,
            });
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var repo2 = new AuditEventRepository(readCtx);
        var loaded = await repo2.GetAsync(eventId);

        Assert.NotNull(loaded);
        // PostgreSQL jsonb normalises whitespace (adds a space after ':'). Compare
        // by stripping spaces so the assertion is format-agnostic.
        Assert.Equal(
            metadata.Replace(" ", ""),
            loaded.Metadata.Replace(" ", ""));
        Assert.Equal("booking.accepted", loaded.EventType);
    }

    // (2) Insert a second audit event (append); both rows exist independently.
    [Fact]
    public async Task Insert_MultipleEvents_AllPersist()
    {
        var tenantId = $"t4-ae-{Guid.NewGuid():N}";
        var eventId1 = $"evt-{Guid.NewGuid():N}";
        var eventId2 = $"evt-{Guid.NewGuid():N}";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var repo = new AuditEventRepository(ctx);
            await repo.AddAsync(new AuditEvent
            {
                TenantId = tenantId,
                EventId = eventId1,
                EventType = "grant.issued",
                Actor = "api",
                SubjectRef = "cust-1",
                Metadata = "{}",
            });
        }

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var repo = new AuditEventRepository(ctx);
            await repo.AddAsync(new AuditEvent
            {
                TenantId = tenantId,
                EventId = eventId2,
                EventType = "grant.consumed",
                Actor = "api",
                SubjectRef = "cust-1",
                Metadata = """{"grant_id":"g1"}""",
            });
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var count = await readCtx.AuditEvents.AsNoTracking()
            .CountAsync(e => e.EventId == eventId1 || e.EventId == eventId2);

        Assert.Equal(2, count);
    }
}
