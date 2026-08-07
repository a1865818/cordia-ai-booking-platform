using Cordia.Application.Common.Authorization;
using Microsoft.Extensions.Options;

namespace Cordia.Tests.UnitTests.BookingOperations;

/// <summary>
/// The asserted-staff-role gate, as the two deployment postures a test needs.
///
/// <para><see cref="Allowed"/> matches Development and the test host, where the console's
/// <c>X-Actor</c> identity is the documented shim. <see cref="Denied"/> matches every other
/// deployment, where an asserted role authorizes nothing and a mutation needs a real grant.</para>
/// </summary>
internal static class AssertedActor
{
    internal static IOptions<AssertedActorOptions> Allowed { get; } =
        Options.Create(new AssertedActorOptions { AllowAssertedStaffRole = true });

    internal static IOptions<AssertedActorOptions> Denied { get; } =
        Options.Create(new AssertedActorOptions { AllowAssertedStaffRole = false });
}
