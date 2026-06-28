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

namespace Cordia.Tests.IntegrationTests.Messaging;

/// <summary>
/// Integration test for <see cref="RetryNotificationCommandHandler"/> (spec 10 FM#6, T5a).
///
/// Seeds a parent BookingOperation + a <c>notifications</c> row directly in state
/// <c>failed_transient</c> with <c>next_eligible_at = now - 1min</c>, version=1, attempt_count=1.
/// Sends <see cref="RetryNotificationCommand"/> via mediator and asserts the row transitions to
/// <c>accepted</c> (stub Sms adapter accepts).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class NotificationRetryTests(PostgresFixture fixture)
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

    /// <summary>
    /// Seeds a FailedTransient notification with <c>next_eligible_at</c> in the past,
    /// dispatches <c>RetryNotificationCommand</c> via mediator, and asserts the row
    /// transitions to <c>accepted</c> with a non-null <c>provider_ref</c>.
    /// </summary>
    [Fact]
    public async Task FailedTransient_Due_SendsViaMediator_TransitionsToAccepted()
    {
        var tenantId = $"t5a-retry-{Guid.NewGuid():N}";
        var notifId = $"notif-retry-{Guid.NewGuid():N}";
        var providerKey = $"pkey-retry-{Guid.NewGuid():N}";
        var messageId = $"msg-retry-{Guid.NewGuid():N}";

        // Neutralize pre-existing failed_transient rows so they don't interfere.
        await using (var cleanupCtx = fixture.CreateContext("t5a-retry-cleanup"))
        {
            await cleanupCtx.Notifications
                .IgnoreQueryFilters()
                .Where(x => x.State == NotificationState.FailedTransient)
                .ExecuteUpdateAsync(b => b.SetProperty(x => x.State, NotificationState.FailedPermanent));
        }

        // Seed a parent operation + FailedTransient notification row.
        string operationId;
        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Booked);

            seedCtx.Notifications.Add(new Notification
            {
                TenantId = tenantId,
                NotificationId = notifId,
                OperationId = operationId,
                EventType = NotificationEvent.BookingConfirmed,
                RecipientRef = "recip-retry-test",
                TemplateVersion = "v1",
                MaterialRevision = 1,
                State = NotificationState.FailedTransient,
                Channel = NotificationChannel.Sms,
                Priority = MessagePriority.High,
                ProviderIdempotencyKey = providerKey,
                ProviderRef = null,
                AttemptCount = 1,
                NextEligibleAt = DateTimeOffset.UtcNow.AddMinutes(-1), // past → eligible
                Version = 1,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            });
            await seedCtx.SaveChangesAsync();
        }

        // Dispatch RetryNotificationCommand via mediator.
        await using var sp = BuildContainer();
        await using (var scope = sp.CreateAsyncScope())
        {
            var tenantCtx = scope.ServiceProvider.GetRequiredService<TenantContext>();
            tenantCtx.TenantId = tenantId;
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            await mediator.Send(new RetryNotificationCommand(tenantId, notifId, messageId));
        }

        // Assert: the specific row is now Accepted with a non-null provider_ref.
        await using var assertCtx = fixture.CreateContext(tenantId);
        var n = await assertCtx.Notifications
            .AsNoTracking()
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.NotificationId == notifId);

        Assert.NotNull(n);
        Assert.Equal(NotificationState.Accepted, n!.State);
        Assert.NotNull(n.ProviderRef);
    }
}
