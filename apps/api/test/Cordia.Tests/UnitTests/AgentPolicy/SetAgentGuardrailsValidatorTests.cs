using Cordia.Application.Handlers.AgentPolicy.SetAgentGuardrails;
using FluentValidation.TestHelper;

namespace Cordia.Tests.UnitTests.AgentPolicy;

[Trait("Category", "Unit")]
public class SetAgentGuardrailsValidatorTests
{
    private readonly SetAgentGuardrailsValidator _validator = new();

    [Fact]
    public async Task EmptyId_Fails()
    {
        var result = await _validator.TestValidateAsync(
            new SetAgentGuardrailsCommand("t1", [new GuardrailToggle("", false)], "owner", "rc", "owner"),
            cancellationToken: TestContext.Current.CancellationToken);
        result.ShouldHaveValidationErrorFor("Guardrails[0].Id");
    }

    [Fact]
    public async Task KnownToggle_Passes()
    {
        var result = await _validator.TestValidateAsync(
            new SetAgentGuardrailsCommand("t1", [new GuardrailToggle("g1", false)], "owner", "rc", "owner"),
            cancellationToken: TestContext.Current.CancellationToken);
        result.ShouldNotHaveAnyValidationErrors();
    }
}
