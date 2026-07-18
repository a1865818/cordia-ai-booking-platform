using Cordia.Application.Handlers.DeadLetters;
using FluentValidation.TestHelper;

namespace Cordia.Tests.UnitTests.Messaging;

/// <summary>
/// Unit tests for <see cref="ReplayDeadLetterValidator"/> (spec 05 §6.1, T7;
/// spec 14 §24.5 operator actor + reason code).
/// </summary>
[Trait("Category", "Unit")]
public class ReplayDeadLetterValidatorTests
{
    private readonly ReplayDeadLetterValidator _validator = new();

    private static ReplayDeadLetterCommand MakeCommand(
        string tenantId = "tenant-abc",
        string messageId = "msg-123",
        string consumerName = "booking.finalizer",
        string actorId = "ops.jane",
        string reasonCode = "provider_outage_recovered",
        string actorRole = "operator")
        => new(tenantId, messageId, consumerName, actorId, reasonCode, actorRole);

    // ── Happy path ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Valid_command_passes()
    {
        var result = await _validator.TestValidateAsync(
            MakeCommand(), cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotHaveAnyValidationErrors();
    }

    // ── Empty TenantId ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Empty_tenant_id_fails()
    {
        var result = await _validator.TestValidateAsync(
            MakeCommand(tenantId: ""), cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.TenantId);
    }

    // ── Empty MessageId ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Empty_message_id_fails()
    {
        var result = await _validator.TestValidateAsync(
            MakeCommand(messageId: ""), cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.MessageId);
    }

    // ── Empty ConsumerName ────────────────────────────────────────────────────────

    [Fact]
    public async Task Empty_consumer_name_fails()
    {
        var result = await _validator.TestValidateAsync(
            MakeCommand(consumerName: ""), cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.ConsumerName);
    }

    // ── Spec 14 §24.5: operator identity + reason code are mandatory ────────────────

    [Fact]
    public async Task Empty_actor_id_fails()
    {
        var result = await _validator.TestValidateAsync(
            MakeCommand(actorId: ""), cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.ActorId);
    }

    [Fact]
    public async Task Empty_reason_code_fails()
    {
        var result = await _validator.TestValidateAsync(
            MakeCommand(reasonCode: ""), cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.ReasonCode);
    }

    [Fact]
    public async Task Empty_actor_role_fails()
    {
        // GD-1/GD-2 (spec 14 §24.5): the staff role (X-Actor-Role) is mandatory.
        var result = await _validator.TestValidateAsync(
            MakeCommand(actorRole: ""), cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.ActorRole);
    }
}
