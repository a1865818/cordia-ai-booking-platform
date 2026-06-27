using Cordia.Application.Handlers.DeadLetters;
using FluentValidation.TestHelper;

namespace Cordia.Tests.UnitTests.Messaging;

/// <summary>
/// Unit tests for <see cref="ReplayDeadLetterValidator"/> (spec 05 §6.1, T7).
/// </summary>
[Trait("Category", "Unit")]
public class ReplayDeadLetterValidatorTests
{
    private readonly ReplayDeadLetterValidator _validator = new();

    // ── Happy path ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Valid_command_passes()
    {
        var cmd = new ReplayDeadLetterCommand(
            TenantId: "tenant-abc",
            MessageId: "msg-123",
            ConsumerName: "booking.finalizer");

        var result = await _validator.TestValidateAsync(
            cmd, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotHaveAnyValidationErrors();
    }

    // ── Empty TenantId ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Empty_tenant_id_fails()
    {
        var cmd = new ReplayDeadLetterCommand(
            TenantId: "",
            MessageId: "msg-123",
            ConsumerName: "booking.finalizer");

        var result = await _validator.TestValidateAsync(
            cmd, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.TenantId);
    }

    // ── Empty MessageId ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Empty_message_id_fails()
    {
        var cmd = new ReplayDeadLetterCommand(
            TenantId: "tenant-abc",
            MessageId: "",
            ConsumerName: "booking.finalizer");

        var result = await _validator.TestValidateAsync(
            cmd, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.MessageId);
    }

    // ── Empty ConsumerName ────────────────────────────────────────────────────────

    [Fact]
    public async Task Empty_consumer_name_fails()
    {
        var cmd = new ReplayDeadLetterCommand(
            TenantId: "tenant-abc",
            MessageId: "msg-123",
            ConsumerName: "");

        var result = await _validator.TestValidateAsync(
            cmd, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.ConsumerName);
    }
}
