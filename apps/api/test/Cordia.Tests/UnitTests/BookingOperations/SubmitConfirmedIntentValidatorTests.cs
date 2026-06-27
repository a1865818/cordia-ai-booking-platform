using Cordia.Application.BookingOperations.SubmitConfirmedIntent;
using Cordia.Domain.Generated;
using FluentValidation.TestHelper;

namespace Cordia.Tests.UnitTests.BookingOperations;

/// <summary>
/// Unit tests for <see cref="SubmitConfirmedIntentValidator"/> (spec 04 §7, §10.3).
/// Tests structural validation rules; semantic hash checks are in the handler.
/// </summary>
[Trait("Category", "Unit")]
public class SubmitConfirmedIntentValidatorTests
{
    private readonly SubmitConfirmedIntentValidator _validator = new();

    // --- Helpers ---

    private static SubmitConfirmedIntentCommand ValidCommand(
        OperationTypeEnum operation = OperationTypeEnum.Create,
        string targetBookingId = "") =>
        new(
            TenantId: "tenant-abc",
            Request: new SubmitConfirmedIntentRequest
            {
                OperationId = "op_123",
                IdempotencyKey = "idem_abc",
                IntentId = "intent_xyz",
                IntentRevision = 1,
                Revision = new IntentRevision
                {
                    IntentId = "intent_xyz",
                    Revision = 1,
                    SchemaVersion = "intent.v1",
                    TenantPolicyVersion = "policy.v1",
                    CanonicalPayload = new BookingRequest
                    {
                        Operation = operation,
                        TargetBookingId = targetBookingId,
                    },
                },
                Confirmation = new Confirmation
                {
                    ConfirmationId = "conf_001",
                    IntentId = "intent_xyz",
                    IntentRevision = 1,
                    IntentPayloadHash = "abc123",
                    SourceTurnId = "turn_001",
                    ReadBackVersion = "v1",
                    ConfirmedAt = DateTimeOffset.UtcNow,
                },
            });

    // ------------------------------------------------------------------
    // Happy path
    // ------------------------------------------------------------------

    [Fact]
    public async Task Valid_create_command_passes()
    {
        var result = await _validator.TestValidateAsync(
            ValidCommand(), cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(OperationTypeEnum.Modify)]
    [InlineData(OperationTypeEnum.Cancel)]
    [InlineData(OperationTypeEnum.Reschedule)]
    public async Task Valid_modify_cancel_reschedule_with_target_booking_id_passes(OperationTypeEnum op)
    {
        var result = await _validator.TestValidateAsync(
            ValidCommand(op, targetBookingId: "bk_existing"),
            cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotHaveAnyValidationErrors();
    }

    // ------------------------------------------------------------------
    // Required field checks
    // ------------------------------------------------------------------

    [Fact]
    public async Task Missing_tenant_id_fails()
    {
        var cmd = ValidCommand() with { TenantId = "" };

        var result = await _validator.TestValidateAsync(
            cmd, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.TenantId);
    }

    [Fact]
    public async Task Missing_operation_id_fails()
    {
        var cmd = ValidCommand();
        cmd.Request.OperationId = "";

        var result = await _validator.TestValidateAsync(
            cmd, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.Request.OperationId);
    }

    [Fact]
    public async Task Missing_idempotency_key_fails()
    {
        var cmd = ValidCommand();
        cmd.Request.IdempotencyKey = "";

        var result = await _validator.TestValidateAsync(
            cmd, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.Request.IdempotencyKey);
    }

    // ------------------------------------------------------------------
    // Modify/cancel/reschedule without target_booking_id → invalid
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(OperationTypeEnum.Modify)]
    [InlineData(OperationTypeEnum.Cancel)]
    [InlineData(OperationTypeEnum.Reschedule)]
    public async Task Modify_cancel_reschedule_without_target_booking_id_fails(OperationTypeEnum op)
    {
        // targetBookingId left empty string (the default in ValidCommand)
        var cmd = ValidCommand(op);

        var result = await _validator.TestValidateAsync(
            cmd, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.Request.Revision.CanonicalPayload.TargetBookingId);
    }

    [Fact]
    public async Task Create_without_target_booking_id_passes()
    {
        // Create does NOT require target_booking_id.
        var cmd = ValidCommand(OperationTypeEnum.Create);

        var result = await _validator.TestValidateAsync(
            cmd, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotHaveValidationErrorFor(x => x.Request.Revision.CanonicalPayload.TargetBookingId);
    }

    // ------------------------------------------------------------------
    // Confirmation not binding to revision → invalid (§8.8, Inv-1)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Confirmation_intent_id_mismatch_fails()
    {
        var cmd = ValidCommand();
        // Confirmation points to a different intent_id than the revision.
        cmd.Request.Confirmation.IntentId = "intent_DIFFERENT";

        var result = await _validator.TestValidateAsync(
            cmd, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.Request.Confirmation.IntentId);
    }

    [Fact]
    public async Task Confirmation_intent_revision_mismatch_fails()
    {
        var cmd = ValidCommand();
        // Confirmation points to revision 99, revision says revision 1.
        cmd.Request.Confirmation.IntentRevision = 99;

        var result = await _validator.TestValidateAsync(
            cmd, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.Request.Confirmation.IntentRevision);
    }
}
