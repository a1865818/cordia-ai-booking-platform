#pragma warning disable xUnit1051
using Cordia.Application.Extensions;
using Cordia.Application.Handlers.Notifications.Retry;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.Extensions;
using Cordia.Infrastructure.Services;
using Cordia.Tests.IntegrationTests.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cordia.Tests.IntegrationTests.Security;

/// <summary>
/// Spec 14 §11 / Inv-13 — "tenant isolation across every repository AND queue
/// consumer": drives a real inbox consumer (RetryNotificationCommand through
/// the full MediatR pipeline incl. InboxDeduplicationBehavior) against a real
/// Postgres with the consumer scoped to tenant A, targeting a row that exists
/// only under tenant B. The consumer must no-op: B's row untouched, and the
/// inbox admission recorded under tenant A only.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class QueueConsumerTenantIsolationTests(PostgresFixture fixture)
{
    private static ServiceProvider BuildContainer()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CordiaDBConnection"] = PostgresFixture.TestConnStr,
                ["Messaging:Publisher:Enabled"] = "false",
                ["Messaging:ScheduledJobPoller:Enabled"] = "false",
                ["Messaging:StepSweeper:Enabled"] = "false",
                ["Messaging:Sla:Enabled"] = "false",
                ["Messaging:NotificationReconcile:Enabled"] = "false",
                ["Messaging:MissingNotificationAudit:Enabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(config);
        services.AddApplication();
        services.AddInfrastructure(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ConsumerScopedToTenantA_CannotTouchTenantBsRow()
    {
        var tenantA = $"t14-qiso-a-{Guid.NewGuid():N}";
        var tenantB = $"t14-qiso-b-{Guid.NewGuid():N}";
        var notifId = $"notif-qiso-{Guid.NewGuid():N}";
        var messageId = $"msg-qiso-{Guid.NewGuid():N}";

        // Seed a retry-eligible notification that exists ONLY under tenant B.
        await using (var seedCtx = fixture.CreateContext(tenantB))
        {
            seedCtx.Notifications.Add(new Notification
            {
                TenantId = tenantB,
                NotificationId = notifId,
                OperationId = null,
                EventType = NotificationEvent.BookingConfirmed,
                RecipientRef = "recip-qiso",
                TemplateVersion = "v1",
                MaterialRevision = 1,
                State = NotificationState.FailedTransient,
                Channel = NotificationChannel.Sms,
                Priority = MessagePriority.High,
                AttemptCount = 1,
                NextEligibleAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                Version = 1,
            });
            await seedCtx.SaveChangesAsync();
        }

        // Dispatch the consumer under TENANT A's scope, targeting B's row id —
        // the envelope-tenant is authoritative (as the transport sets it).
        await using var sp = BuildContainer();
        await using (var scope = sp.CreateAsyncScope())
        {
            var tenantCtx = scope.ServiceProvider.GetRequiredService<TenantContext>();
            tenantCtx.SetTenant(tenantA);
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            await mediator.Send(new RetryNotificationCommand(tenantA, notifId, messageId));
        }

        // Tenant B's row is byte-for-byte untouched.
        await using var assertCtx = fixture.CreateContext(tenantB);
        var b = await assertCtx.Notifications
            .AsNoTracking()
            .IgnoreQueryFilters()
            .SingleAsync(x => x.TenantId == tenantB && x.NotificationId == notifId);
        Assert.Equal(NotificationState.FailedTransient, b.State);
        Assert.Equal(1L, b.Version);
        Assert.Equal(1, b.AttemptCount);
        Assert.Null(b.ProviderRef);

        // The inbox admission landed under tenant A only (duplicate-safe path),
        // never under tenant B.
        var inboxA = await assertCtx.ConsumerInbox
            .AsNoTracking()
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(i =>
                i.TenantId == tenantA && i.MessageId == messageId && i.ConsumerName == "notification.sender");
        Assert.NotNull(inboxA);
        var inboxB = await assertCtx.ConsumerInbox
            .AsNoTracking()
            .IgnoreQueryFilters()
            .AnyAsync(i => i.TenantId == tenantB && i.MessageId == messageId);
        Assert.False(inboxB);
    }
}
