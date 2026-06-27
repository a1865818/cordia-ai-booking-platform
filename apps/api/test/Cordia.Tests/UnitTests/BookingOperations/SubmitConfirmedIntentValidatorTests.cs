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
                    PayloadHash = "hash_xyz",
                    SourceSessionId = "sess_001",
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
                    Identity = "anon-session",
                    SourceTurnId = "turn_001",
                    ReadBackVersion = "v1",
                    VerificationLevel = "unverified",
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

    // ------------------------------------------------------------------
    // Null-Request regression (Fix 1): null Request must yield 400, not NRE
    // ------------------------------------------------------------------

    [Fact]
    public async Task Null_request_body_fails_without_NullReferenceException()
    {
        // A null Request body must produce a validation failure (HTTP 400),
        // NOT a NullReferenceException (HTTP 500).
        var cmd = new SubmitConfirmedIntentCommand("tenant-abc", null!);

        // Must not throw; must not be valid.
        var result = await _validator.TestValidateAsync(
            cmd, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Valid_request_still_passes_after_null_guard()
    {
        // Confirm the null-guard wrapping does not break the happy-path.
        var result = await _validator.TestValidateAsync(
            ValidCommand(), cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotHaveAnyValidationErrors();
    }

    // ------------------------------------------------------------------
    // Required confirmation evidence fields (Inv-1, Fix 1)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Missing_confirmation_id_fails()
    {
        // The 500-causing case: null ConfirmationId reaches AddConfirmationAsync
        // and hits the intent_confirmations PK constraint → DB violation → 500.
        // After this fix it must produce a 400 instead.
        var cmd = ValidCommand();
        cmd.Request.Confirmation.ConfirmationId = "";

        var result = await _validator.TestValidateAsync(
            cmd, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.Request.Confirmation.ConfirmationId);
    }

    [Fact]
    public async Task Missing_confirmation_source_turn_id_fails()
    {
        var cmd = ValidCommand();
        cmd.Request.Confirmation.SourceTurnId = "";

        var result = await _validator.TestValidateAsync(
            cmd, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.Request.Confirmation.SourceTurnId);
    }

    [Fact]
    public async Task Missing_revision_payload_hash_fails()
    {
        var cmd = ValidCommand();
        cmd.Request.Revision.PayloadHash = "";

        var result = await _validator.TestValidateAsync(
            cmd, cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldHaveValidationErrorFor(x => x.Request.Revision.PayloadHash);
    }
}
