using Cordia.Application.Extensions;
using Cordia.Application.Handlers.Verification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cordia.Tests.UnitTests.Security;

/// <summary>
/// The verification-code pepper fails closed (#71). The old guard refused a missing pepper only when
/// the environment was literally "Production", so Staging hashed real codes under the pepper
/// committed to this repository.
/// </summary>
[Trait("Category", "Unit")]
public class VerificationCodePepperTests
{
    [Fact]
    public void Hash_RefusesWhenNoPepperIsConfigured_AndTheLocalOneIsNotAllowed()
        => Assert.Throws<InvalidOperationException>(
            () => VerificationCode.Hash(new VerificationCodeOptions(), "ver_1", "123456"));

    [Fact]
    public void Hash_UsesTheLocalPepper_OnlyWhenExplicitlyAllowed()
    {
        var hash = VerificationCode.Hash(new VerificationCodeOptions { AllowLocalPepper = true }, "ver_1", "123456");

        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    /// <summary>A configured pepper must actually be the key — otherwise the gate protects nothing.</summary>
    [Fact]
    public void Hash_UsesTheConfiguredPepper_InPreferenceToTheLocalOne()
    {
        var local = VerificationCode.Hash(new VerificationCodeOptions { AllowLocalPepper = true }, "ver_1", "123456");
        var real = VerificationCode.Hash(
            new VerificationCodeOptions { AllowLocalPepper = true, CodePepper = "a-real-deployment-pepper" },
            "ver_1", "123456");

        Assert.NotEqual(local, real);
    }

    private static IOptions<VerificationCodeOptions> Resolve(Dictionary<string, string?> config)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        services.AddApplication();
        return services.BuildServiceProvider().GetRequiredService<IOptions<VerificationCodeOptions>>();
    }

    /// <summary>The host refuses to start rather than 500ing on the first verification.</summary>
    [Fact]
    public void Options_FailValidation_WithNeitherAPepperNorAnOptIn()
    {
        Assert.SkipWhen(
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VerificationCodeOptions.PepperEnvironmentVariable)),
            "A real pepper is set in this process environment, so the refusal cannot be observed.");

        var options = Resolve(new());

        Assert.Throws<OptionsValidationException>(() => options.Value);
    }

    [Fact]
    public void Options_PassValidation_WithAConfiguredPepper()
        => Assert.Equal(
            "cfg-pepper",
            Resolve(new() { ["Verification:CodePepper"] = "cfg-pepper" }).Value.CodePepper);

    [Fact]
    public void Options_PassValidation_WithAnExplicitOptIn()
        => Assert.True(Resolve(new() { ["Verification:AllowLocalPepper"] = "true" }).Value.AllowLocalPepper);
}
