using Cordia.Application.Handlers.AgentKnowledge.CreateAgentKnowledge;
using Cordia.Application.Handlers.AgentKnowledge.UpdateAgentKnowledge;
using FluentValidation.TestHelper;

namespace Cordia.Tests.UnitTests.AgentKnowledge;

[Trait("Category", "Unit")]
public class AgentKnowledgeValidatorTests
{
    [Fact]
    public async Task Create_EmptyQuestion_Fails()
    {
        var result = await new CreateAgentKnowledgeValidator().TestValidateAsync(
            new CreateAgentKnowledgeCommand("t1", "", null, "owner", "rc", "owner"),
            cancellationToken: TestContext.Current.CancellationToken);
        result.ShouldHaveValidationErrorFor(x => x.Question);
    }

    [Fact]
    public async Task Create_QuestionOnly_Passes()
    {
        var result = await new CreateAgentKnowledgeValidator().TestValidateAsync(
            new CreateAgentKnowledgeCommand("t1", "Where do I park?", null, "owner", "rc", "owner"),
            cancellationToken: TestContext.Current.CancellationToken);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public async Task Update_UnknownStatus_Fails()
    {
        var result = await new UpdateAgentKnowledgeValidator().TestValidateAsync(
            new UpdateAgentKnowledgeCommand("t1", "knw_1", "ans", "draft", "owner", "rc", "owner"),
            cancellationToken: TestContext.Current.CancellationToken);
        result.ShouldHaveValidationErrorFor(x => x.Status);
    }

    [Fact]
    public async Task Update_NeedsAnswerStatus_Passes()
    {
        var result = await new UpdateAgentKnowledgeValidator().TestValidateAsync(
            new UpdateAgentKnowledgeCommand("t1", "knw_1", null, "needs-answer", "owner", "rc", "owner"),
            cancellationToken: TestContext.Current.CancellationToken);
        result.ShouldNotHaveAnyValidationErrors();
    }
}
