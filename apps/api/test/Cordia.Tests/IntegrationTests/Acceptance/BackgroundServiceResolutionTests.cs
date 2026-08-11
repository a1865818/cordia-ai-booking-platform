#pragma warning disable xUnit1051
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Cordia.Tests.IntegrationTests.Acceptance;

/// <summary>
/// Every dependency a background sweeper resolves per tick must be resolvable from the real
/// container.
///
/// <para><b>Why this suite exists.</b> Background loops catch and log every exception
/// (<c>PollingLoop.RunAsync</c>), and <c>RetentionSweeper</c> additionally catches per tenant so
/// one tenant's failure cannot block another's. Both are correct — and together they mean a
/// missing DI registration does not crash anything. It throws on every tick, gets logged, and the
/// sweeper silently never does its job.</para>
///
/// <para>That is not hypothetical: adding the guest-contact erase to the retention sweep did
/// exactly this, and the only visible symptom was a test asserting a lower pruned count. In
/// production there would have been no symptom at all — retention would simply have stopped.</para>
///
/// <para>Resolving from <c>fixture.Host.Services</c> rather than a hand-built collection is the
/// point: a hand-built one can be taught about a service the real composition root never
/// registers, which is precisely the bug being guarded against.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class BackgroundServiceResolutionTests(AcceptanceApiFixture fixture)
{
    /// <summary>The services a sweeper resolves from a FRESH scope on each tick.</summary>
    public static TheoryData<Type> PerTickScopedDependencies() =>
    [
        // Retention sweep (spec 14 §24.3). IGuestContactRepository is the one that was missing.
        typeof(IRetentionSweeper),
        typeof(IGuestContactRepository),
        // Stalled-ingestion monitor (docs §12 gap 8).
        typeof(IMenuIngestionMonitor),
        typeof(IVendorCatalogRepository),
        // Pre-order recovery backstop (spec 11 §5.3).
        typeof(IPreorderRecoverySweeper),
    ];

    [Theory]
    [MemberData(nameof(PerTickScopedDependencies))]
    public void EveryPerTickDependencyResolvesFromTheRealContainer(Type serviceType)
    {
        using var scope = fixture.Host.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();

        // GetRequiredService, not GetService: the sweepers call the throwing overload, so a null
        // here would be a false pass — the production call would still throw.
        var resolved = scope.ServiceProvider.GetRequiredService(serviceType);

        Assert.NotNull(resolved);
    }

    /// <summary>
    /// The hosted services themselves must be constructible. A sweeper that cannot be built never
    /// starts, and <c>BackgroundService</c> failures during startup are easy to miss in logs.
    /// </summary>
    [Fact]
    public void TheStalledIngestionMonitorHostedServiceIsRegistered()
    {
        var hosted = fixture.Host.Services
            .GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .ToList();

        Assert.Contains(hosted, h => h is MenuIngestionMonitorHostedService);
    }
}
