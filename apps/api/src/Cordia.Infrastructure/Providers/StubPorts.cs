using Microsoft.Extensions.Configuration;

namespace Cordia.Infrastructure.Providers;

/// <summary>
/// One port served by a stand-in rather than a real integration (#75, decision D1).
/// </summary>
/// <param name="Port">The interface, as an operator would search for it.</param>
/// <param name="Implementation">The stub class registered for it.</param>
/// <param name="ConfigKey">The flag that says "yes, run on the stub". Defaults to true.</param>
/// <param name="Consequence">What is not really happening while the stub serves the port.</param>
/// <param name="DefaultOn">Whether the flag defaults on when unset.</param>
public sealed record StubPort(
    string Port,
    string Implementation,
    string ConfigKey,
    string Consequence,
    bool DefaultOn = true);

/// <summary>
/// The catalogue of ports still served by stubs, and what configuration says about them (#75).
///
/// <para><b>Why visibility and not an environment gate.</b> The filed remedy was to register these
/// only outside Production. Five of the six have no real implementation to fall back to, so that
/// would not harden a deployment — it would stop it resolving booking, verification, validation,
/// scanning and pre-order reservation at all. And ~20 integration suites build the container with no
/// environment, which <c>AddInfrastructure</c> deliberately reads as "a deployment". So each stub
/// gets its own flag, defaulting on so nothing changes, and <c>Program.cs</c> prints ONE warning
/// outside Development naming every stub still in service and what it means.</para>
///
/// <para><b>A flag set to false is refused, not ignored.</b> It is a request for a real
/// implementation that does not exist yet. Silently keeping the stub would leave the operator
/// believing they had turned it off.</para>
///
/// <para>The menu extractor is listed too, under its existing <c>LangGraph:UseStubExtractor</c> key —
/// but that one defaults OFF and does have a real client, so it can be switched off.</para>
/// </summary>
public static class StubPorts
{
    public static readonly IReadOnlyList<StubPort> All =
    [
        new("IBookingProviderAdapter", nameof(StubBookingProviderAdapter), "Stubs:BookingProvider",
            "bookings are \"created\" in process memory — lost on restart and never sent to a provider"),
        new("IVerificationService", nameof(NoUserActionRequiredService), "Stubs:VerificationService",
            "no operation is ever held for a required user action"),
        new("IValidationPolicy", nameof(AlwaysValidPolicy), "Stubs:ValidationPolicy",
            "every booking request passes policy validation"),
        new("IArtifactScanner", nameof(StubArtifactScanner), "Stubs:ArtifactScanner",
            "uploaded menu artifacts are not malware-scanned"),
        new("IPreorderItemReservation", nameof(StubPreorderItemReservation), "Stubs:PreorderItemReservation",
            "pre-order items are not reserved in any point-of-sale system"),
        new("IExternalInventoryAdapter", nameof(StubExternalInventoryAdapter), "Stubs:ExternalInventory",
            "external-provider inventory is simulated (not on the live booking path)"),
    ];

    /// <summary>The menu extractor's existing flag — the one stub with a real implementation.</summary>
    public static readonly StubPort MenuExtractor = new(
        "IIsolatedMenuExtractor", "StubIsolatedMenuExtractor", "LangGraph:UseStubExtractor",
        "uploaded menus are never read; every ingestion yields the same sample item",
        DefaultOn: false);

    /// <summary>Every stub currently in service under this configuration.</summary>
    public static IReadOnlyList<StubPort> Active(IConfiguration configuration)
        => All.Append(MenuExtractor)
            .Where(p => configuration.GetValue(p.ConfigKey, p.DefaultOn))
            .ToList();

    /// <summary>
    /// Stubs switched off for which no real implementation exists. Non-empty means the host must not
    /// start: the configuration asks for something this build cannot provide.
    /// </summary>
    public static IReadOnlyList<StubPort> RefusedOptOuts(IConfiguration configuration)
        => All.Where(p => !configuration.GetValue(p.ConfigKey, p.DefaultOn)).ToList();

    /// <summary>One line per active stub: port, implementation, consequence. Carries no configuration values.</summary>
    public static string Describe(IEnumerable<StubPort> ports)
        => string.Join("; ", ports.Select(p => $"{p.Port} → {p.Implementation} ({p.Consequence})"));
}
