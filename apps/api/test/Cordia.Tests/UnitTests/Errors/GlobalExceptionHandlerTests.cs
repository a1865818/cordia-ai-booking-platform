using System.Text.Json;
using Cordia.Api.Errors;
using Cordia.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cordia.Tests.UnitTests.Errors;

/// <summary>
/// Unit tests for <see cref="GlobalExceptionHandler"/>.
/// Verifies that each typed domain exception maps to the correct HTTP status code
/// and that unmapped exceptions return false (delegated to the default 500 pipeline).
/// </summary>
[Trait("Category", "Unit")]
public class GlobalExceptionHandlerTests
{
    private readonly GlobalExceptionHandler _handler =
        new(NullLogger<GlobalExceptionHandler>.Instance);

    private static DefaultHttpContext CreateContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    // ------------------------------------------------------------------
    // ConfirmationHashMismatchException → 422
    // ------------------------------------------------------------------
    [Fact]
    public async Task ConfirmationHashMismatch_Returns422()
    {
        var ctx = CreateContext();
        var ex = new ConfirmationHashMismatchException("Hash does not match");

        var handled = await _handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, ctx.Response.StatusCode);
    }

    // ------------------------------------------------------------------
    // IntentPolicyViolationException → 422
    // ------------------------------------------------------------------
    [Fact]
    public async Task IntentPolicyViolation_Returns422()
    {
        var ctx = CreateContext();
        var ex = new IntentPolicyViolationException("Policy check failed");

        var handled = await _handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, ctx.Response.StatusCode);
    }

    // ------------------------------------------------------------------
    // IdempotencyConflictException → 409 with snake_case JSON body
    // ------------------------------------------------------------------
    [Fact]
    public async Task IdempotencyConflict_Returns409WithSnakeCaseBody()
    {
        var ctx = CreateContext();
        var ex = new IdempotencyConflictException(
            idempotencyKey: "key-abc",
            existingOperationId: "op-xyz",
            message: "Key already bound");

        var handled = await _handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, ctx.Response.StatusCode);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = JsonDocument.Parse(ctx.Response.Body);
        var root = doc.RootElement;

        Assert.Equal("key-abc", root.GetProperty("idempotency_key").GetString());
        Assert.Equal("op-xyz", root.GetProperty("existing_operation_id").GetString());
        Assert.Equal("Key already bound", root.GetProperty("message").GetString());
    }

    // ------------------------------------------------------------------
    // PoisonMessageException → 422
    // ------------------------------------------------------------------
    [Fact]
    public async Task PoisonMessage_Returns422()
    {
        var ctx = CreateContext();
        var ex = new PoisonMessageException("Message cannot be processed");

        var handled = await _handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, ctx.Response.StatusCode);
    }

    // ------------------------------------------------------------------
    // UnsupportedSchemaVersionException → 422
    // ------------------------------------------------------------------
    [Fact]
    public async Task UnsupportedSchemaVersion_Returns422()
    {
        var ctx = CreateContext();
        var ex = new UnsupportedSchemaVersionException("Unknown schema version v99");

        var handled = await _handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, ctx.Response.StatusCode);
    }

    // ------------------------------------------------------------------
    // DeadLetterNotFoundException → 404
    // ------------------------------------------------------------------
    [Fact]
    public async Task DeadLetterNotFound_Returns404()
    {
        var ctx = CreateContext();
        var ex = new DeadLetterNotFoundException("Dead-letter row not found");

        var handled = await _handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
    }

    // ------------------------------------------------------------------
    // InvalidStateTransitionException → 409
    // ------------------------------------------------------------------
    [Fact]
    public async Task InvalidStateTransition_Returns409()
    {
        var ctx = CreateContext();
        var ex = new InvalidStateTransitionException(
            Cordia.Domain.Enums.OperationStatus.Accepted,
            Cordia.Domain.Enums.OperationStatus.Booked);

        var handled = await _handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, ctx.Response.StatusCode);
    }

    // ------------------------------------------------------------------
    // OperationConflictException → 409
    // ------------------------------------------------------------------
    [Fact]
    public async Task OperationConflict_Returns409()
    {
        var ctx = CreateContext();
        var ex = new OperationConflictException("op-123", "Optimistic concurrency lost");

        var handled = await _handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task VerificationRequired_Returns403()
    {
        var ctx = CreateContext();
        var ex = new VerificationRequiredException(
            "tenant-1",
            "cust-1",
            Cordia.Domain.Generated.VerificationActionEnum.Modify,
            "booking-1");

        var handled = await _handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    // ------------------------------------------------------------------
    // Spec 14 typed security exceptions (§24.1, §24.2, §24.5)
    // ------------------------------------------------------------------
    [Fact]
    public async Task TenantScopeMissing_Returns400_WithMissingTenantTitle()
    {
        var ctx = CreateContext();
        var ex = new TenantScopeMissingException();

        var handled = await _handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);

        // Wire shape must match the controllers' historical hand-rolled 400.
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = JsonDocument.Parse(ctx.Response.Body);
        Assert.Equal("Missing tenant", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal("X-Tenant-Id header is required.", doc.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ConsentRequired_Returns409()
    {
        var ctx = CreateContext();
        var ex = new ConsentRequiredException("recording", "sess-1");

        var handled = await _handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task AuthorizationFailed_Returns403()
    {
        var ctx = CreateContext();
        var ex = new AuthorizationFailedException("catalog.published", "Actor and reason code are required.");

        var handled = await _handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task StaleBookingVersion_Returns409()
    {
        var ctx = CreateContext();
        var ex = new StaleBookingVersionException("booking-1", expectedVersion: 1, currentVersion: 2);

        var handled = await _handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, ctx.Response.StatusCode);
    }

    // ------------------------------------------------------------------
    // Unmapped exception → returns false (default 500 pipeline takes over)
    // ------------------------------------------------------------------
    [Fact]
    public async Task UnmappedException_ReturnsFalse()
    {
        var ctx = CreateContext();
        var ex = new InvalidOperationException("Something unexpected");

        var handled = await _handler.TryHandleAsync(ctx, ex, CancellationToken.None);

        Assert.False(handled);
        // Status code must NOT have been set to a mapped value (200 is the default).
        Assert.NotEqual(StatusCodes.Status409Conflict, ctx.Response.StatusCode);
        Assert.NotEqual(StatusCodes.Status422UnprocessableEntity, ctx.Response.StatusCode);
    }
}
