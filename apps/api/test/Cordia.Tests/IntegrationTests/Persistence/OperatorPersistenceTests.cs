#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Infrastructure.Repositories;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class OperatorPersistenceTests(PostgresFixture fixture)
{
    [Fact]
    public async Task AddAndGet_RoundTripsOperatorAndSession()
    {
        var tenantId = $"t-op-{Guid.NewGuid():N}";
        var operatorId = $"opr-{Guid.NewGuid():N}";
        var sessionId = $"sess-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var operators = new OperatorRepository(ctx);
            await operators.AddAsync(new Operator
            {
                TenantId = tenantId,
                OperatorId = operatorId,
                Email = "owner@demo.local",
                PasswordHash = "hash",
                Role = "owner",
                Status = "active",
                CreatedAt = now,
                UpdatedAt = now,
            });

            var sessions = new OperatorSessionRepository(ctx);
            await sessions.AddAsync(new OperatorSession
            {
                TenantId = tenantId,
                SessionId = sessionId,
                OperatorId = operatorId,
                IssuedAt = now,
                ExpiresAt = now.AddHours(8),
                CorrelationId = "corr-1",
            });
        }

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var operators = new OperatorRepository(ctx);
            var byEmail = await operators.GetByEmailAsync(tenantId, "owner@demo.local");
            var byId = await operators.GetByIdAsync(tenantId, operatorId);

            Assert.NotNull(byEmail);
            Assert.Equal(operatorId, byEmail.OperatorId);
            Assert.Equal("owner", byEmail.Role);
            Assert.Equal(operatorId, byId?.OperatorId);

            var sessions = new OperatorSessionRepository(ctx);
            var session = await sessions.GetAsync(tenantId, sessionId);

            Assert.NotNull(session);
            Assert.Equal(operatorId, session.OperatorId);
            Assert.Null(session.RevokedAt);

            await sessions.RevokeAsync(tenantId, sessionId);
        }

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var revoked = await new OperatorSessionRepository(ctx).GetAsync(tenantId, sessionId);
            Assert.NotNull(revoked?.RevokedAt);
        }
    }
}
