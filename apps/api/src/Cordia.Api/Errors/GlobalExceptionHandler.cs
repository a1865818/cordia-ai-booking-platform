using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Errors;

/// <summary>
/// Global exception handler that maps typed domain exceptions to HTTP responses.
///
/// Responsibility split:
///   - HTTP 400 Bad Request: <see cref="ValidationException"/> thrown by the MediatR
///     <c>ValidationPipelineBehavior</c> (structural failures from FluentValidation validators).
///   - HTTP 422 Unprocessable Entity: semantic/policy/hash failures from typed domain exceptions.
///   - HTTP 409 Conflict: idempotency key bound to a different payload.
///   - All other exceptions: return false so the default pipeline produces HTTP 500.
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>
    /// Stable discriminator for a 403 a client can resolve by obtaining a verification grant
    /// (spec 08, Inv-11). Public so tests and the frontend contract pin to one literal.
    /// </summary>
    public const string VerificationRequiredCode = "verification_required";


    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        switch (exception)
        {
            case ValidationException ex:
                // FluentValidation structural failures from the MediatR ValidationPipelineBehavior.
                logger.LogInformation(ex, "Validation failure");
                var errors = ex.Errors
                    .GroupBy(e => e.PropertyName, StringComparer.Ordinal)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(e => e.ErrorMessage).ToArray(),
                        StringComparer.Ordinal);
                var validationProblem = new ValidationProblemDetails(errors)
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "One or more validation errors occurred.",
                };
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                httpContext.Response.ContentType = "application/problem+json";
                await httpContext.Response.WriteAsJsonAsync(validationProblem, cancellationToken: cancellationToken);
                return true;

            case ConfirmationHashMismatchException ex:
                logger.LogWarning(ex, "Confirmation hash mismatch");
                await WriteProblemAsync(httpContext, StatusCodes.Status422UnprocessableEntity,
                    "Confirmation hash mismatch", ex.Message, cancellationToken);
                return true;

            case IntentPolicyViolationException ex:
                logger.LogWarning(ex, "Intent policy violation");
                await WriteProblemAsync(httpContext, StatusCodes.Status422UnprocessableEntity,
                    "Intent policy violation", ex.Message, cancellationToken);
                return true;

            case PoisonMessageException ex:
                logger.LogWarning(ex, "Poison message");
                await WriteProblemAsync(httpContext, StatusCodes.Status422UnprocessableEntity,
                    "Poison message", ex.Message, cancellationToken);
                return true;

            case UnsupportedSchemaVersionException ex:
                logger.LogWarning(ex, "Unsupported schema version");
                await WriteProblemAsync(httpContext, StatusCodes.Status422UnprocessableEntity,
                    "Unsupported schema version", ex.Message, cancellationToken);
                return true;

            case DeadLetterNotFoundException ex:
                logger.LogWarning(ex, "Dead-letter message not found");
                await WriteProblemAsync(httpContext, StatusCodes.Status404NotFound,
                    "Dead-letter message not found", ex.Message, cancellationToken);
                return true;

            case IdempotencyConflictException ex:
                logger.LogWarning(ex, "Idempotency conflict: key={IdempotencyKey}", ex.IdempotencyKey);
                await WriteIdempotencyConflictAsync(httpContext, ex, cancellationToken);
                return true;

            case TenantScopeMissingException ex:
                // Same wire shape as the controllers' historical hand-rolled 400 (spec 14 §24.1).
                logger.LogInformation(ex, "Tenant scope missing");
                await WriteProblemAsync(httpContext, StatusCodes.Status400BadRequest,
                    "Missing tenant", ex.Message, cancellationToken);
                return true;

            case TenantNotResolvedException ex:
                // Spec-15 §7 fail-closed ingress: zero active endpoints matched the channel signal.
                // Generic customer-facing body — NEVER 500, never a fallback tenant, never echo the
                // internal reason or the signal value (Inv-12/Inv-13). Ambiguity is indistinguishable
                // from "not found" to the caller.
                logger.LogWarning(ex, "Tenant not resolved at channel ingress");
                await WriteProblemAsync(httpContext, StatusCodes.Status422UnprocessableEntity,
                    "Unable to reach business", "We couldn't connect you to this business.", cancellationToken);
                return true;

            case AmbiguousEndpointException ex:
                // Spec-15 §7 fail-closed ingress: the signal matched more than one active endpoint.
                // Same generic body as tenant_not_resolved (do not reveal the ambiguity); alerted via
                // logs/metrics (§25.4). Never guess a tenant.
                logger.LogError(ex, "Ambiguous channel endpoint at ingress");
                await WriteProblemAsync(httpContext, StatusCodes.Status422UnprocessableEntity,
                    "Unable to reach business", "We couldn't connect you to this business.", cancellationToken);
                return true;

            case TenantContextVerificationException ex:
                // Spec-15 Phase 4 middleware: a presented signed TenantContext failed verification
                // (unknown/retired kid, bad signature, expired, wrong typ, malformed). Reject as 401;
                // the message is already a fixed generic string (no secret/kid/claim leak, Inv-12).
                logger.LogWarning(ex, "Tenant context verification failed");
                await WriteProblemAsync(httpContext, StatusCodes.Status401Unauthorized,
                    "Invalid tenant context", ex.Message, cancellationToken);
                return true;

            case ConsentRequiredException ex:
                logger.LogWarning(ex, "Consent required: scope={RequiredScope} subject={SubjectRef}",
                    ex.RequiredScope, ex.SubjectRef);
                await WriteProblemAsync(httpContext, StatusCodes.Status409Conflict,
                    "Consent required", ex.Message, cancellationToken);
                return true;

            case AuthorizationFailedException ex:
                logger.LogWarning(ex, "Authorization failed: action={ActionName}", ex.ActionName);
                await WriteProblemAsync(httpContext, StatusCodes.Status403Forbidden,
                    "Authorization failed", ex.Message, cancellationToken);
                return true;

            case VerificationRequiredException ex:
                logger.LogWarning(ex, "Verification required: tenant={TenantId} customer={CustomerRef} action={Action} booking={TargetBookingId}",
                    ex.TenantId, ex.CustomerRef, ex.Action, ex.TargetBookingId);
                // Carries what a client needs to actually resolve this: which D5 action to run
                // step-up for, and against which booking. Deliberately NOT emitted for
                // AuthorizationFailedException, which is also a 403 but means "your role does not
                // permit this" — no amount of guest verification fixes that.
                // `customer_ref` is intentionally omitted: it is the identity the caller has not
                // yet proven a right to see, and this body is returned before that proof exists.
                // FIXED detail, never `ex.Message`. The exception message interpolates the
                // `customer_ref` for the server's own logs, and putting it in the response handed
                // the caller back the very identity they have not yet proven a right to see —
                // turning a 403 into an identity oracle. The machine-readable extensions below
                // carry everything a client legitimately needs to resolve the challenge.
                await WriteProblemAsync(httpContext, StatusCodes.Status403Forbidden,
                    "Verification required",
                    "Verification is required before this action can be performed.",
                    cancellationToken,
                    new Dictionary<string, object?>
                    {
                        ["code"] = VerificationRequiredCode,
                        ["action"] = EnumWireNames.ToWireName(ex.Action),
                        ["target_booking_id"] = ex.TargetBookingId,
                    });
                return true;

            case StaleBookingVersionException ex:
                logger.LogWarning(ex, "Stale booking version: bookingId={BookingId} expected={ExpectedVersion} current={CurrentVersion}",
                    ex.BookingId, ex.ExpectedVersion, ex.CurrentVersion);
                await WriteProblemAsync(httpContext, StatusCodes.Status409Conflict,
                    "Stale booking version", ex.Message, cancellationToken);
                return true;

            case InvalidStateTransitionException ex:
                logger.LogWarning(ex, "Invalid state transition: {From} -> {To}", ex.From, ex.To);
                await WriteProblemAsync(httpContext, StatusCodes.Status409Conflict,
                    "Invalid state transition", ex.Message, cancellationToken);
                return true;

            case InvalidNotificationStateTransitionException ex:
                logger.LogWarning(ex, "Invalid notification state transition: {From} -> {To}", ex.From, ex.To);
                await WriteProblemAsync(httpContext, StatusCodes.Status409Conflict,
                    "Invalid notification state transition", ex.Message, cancellationToken);
                return true;

            case InvalidCatalogStateTransitionException ex:
                logger.LogWarning(ex, "Invalid catalog state transition: {From} -> {To}", ex.From, ex.To);
                await WriteProblemAsync(httpContext, StatusCodes.Status409Conflict,
                    "Invalid catalog state transition", ex.Message, cancellationToken);
                return true;

            case ConversationSessionNotFoundException ex:
                logger.LogWarning(ex, "Conversation session not found: tenant={TenantId} session={SessionId}",
                    ex.TenantId, ex.SessionId);
                await WriteProblemAsync(httpContext, StatusCodes.Status404NotFound,
                    "Conversation session not found", ex.Message, cancellationToken);
                return true;

            case CatalogNotFoundException ex:
                logger.LogWarning(ex, "Vendor catalog not found: {CatalogId}", ex.CatalogId);
                await WriteProblemAsync(httpContext, StatusCodes.Status404NotFound,
                    "Vendor catalog not found", ex.Message, cancellationToken);
                return true;

            case CatalogItemNotFoundException ex:
                logger.LogWarning(ex, "Catalog item not found: {ItemId}", ex.ItemId);
                await WriteProblemAsync(httpContext, StatusCodes.Status404NotFound,
                    "Catalog item not found", ex.Message, cancellationToken);
                return true;

            case InvalidWaitlistOfferTransitionException ex:
                logger.LogWarning(ex, "Invalid waitlist offer transition: {From} -> {To}", ex.From, ex.To);
                await WriteProblemAsync(httpContext, StatusCodes.Status409Conflict,
                    "Invalid waitlist offer transition", ex.Message, cancellationToken);
                return true;

            case WaitlistEnrollmentNotFoundException ex:
                logger.LogWarning(ex, "Waitlist enrollment not found: {EnrollmentId}", ex.EnrollmentId);
                await WriteProblemAsync(httpContext, StatusCodes.Status404NotFound,
                    "Waitlist enrollment not found", ex.Message, cancellationToken);
                return true;

            case WaitlistOfferNotFoundException ex:
                logger.LogWarning(ex, "Waitlist offer not found: {OfferId}", ex.OfferId);
                await WriteProblemAsync(httpContext, StatusCodes.Status404NotFound,
                    "Waitlist offer not found", ex.Message, cancellationToken);
                return true;

            case InvalidPreorderSagaTransitionException ex:
                logger.LogWarning(ex, "Invalid pre-order saga transition: {From} -> {To}", ex.From, ex.To);
                await WriteProblemAsync(httpContext, StatusCodes.Status409Conflict,
                    "Invalid pre-order saga transition", ex.Message, cancellationToken);
                return true;

            case PreorderSagaNotFoundException ex:
                logger.LogWarning(ex, "Pre-order saga not found: {SagaId}", ex.SagaId);
                await WriteProblemAsync(httpContext, StatusCodes.Status404NotFound,
                    "Pre-order saga not found", ex.Message, cancellationToken);
                return true;

            case CatalogNotEditableException ex:
                logger.LogWarning(ex, "Vendor catalog not editable: {CatalogId} ({Status})", ex.CatalogId, ex.Status);
                await WriteProblemAsync(httpContext, StatusCodes.Status409Conflict,
                    "Vendor catalog not editable", ex.Message, cancellationToken);
                return true;

            case CatalogHasNoItemsException ex:
                logger.LogWarning(ex, "Vendor catalog has no items: {CatalogId}", ex.CatalogId);
                await WriteProblemAsync(httpContext, StatusCodes.Status409Conflict,
                    "Vendor catalog has no items", ex.Message, cancellationToken);
                return true;

            case OperationConflictException ex:
                logger.LogWarning(ex, "Operation conflict: operationId={OperationId}", ex.OperationId);
                await WriteProblemAsync(httpContext, StatusCodes.Status409Conflict,
                    "Operation conflict", ex.Message, cancellationToken);
                return true;

            default:
                // Unmapped exception — let the default pipeline produce 500 without leaking internals.
                logger.LogError(exception, "Unhandled exception");
                return false;
        }
    }

    private static async Task WriteProblemAsync(
        HttpContext httpContext,
        int statusCode,
        string title,
        string detail,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, object?>? extensions = null)
    {
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
        };

        // Machine-readable discriminators belong in `extensions` (RFC 9457 §3.2). `title` is prose
        // for a human — a client that branches on it is branching on copy, and copy changes.
        if (extensions is not null)
        {
            foreach (var (key, value) in extensions)
                problem.Extensions[key] = value;
        }

        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/problem+json";
        // No explicit options: that routes through ASP.NET's ProblemDetails converter, which
        // FLATTENS `Extensions` into the object per RFC 9457. Passing a hand-rolled
        // JsonSerializerOptions serializes ProblemDetails as a plain POCO instead, burying the
        // discriminators under a nested "extensions" node no RFC-following client looks in.
        // (The ValidationProblemDetails branch above already relies on this for its `errors`.)
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken: cancellationToken);
    }

    private static async Task WriteIdempotencyConflictAsync(
        HttpContext httpContext,
        IdempotencyConflictException ex,
        CancellationToken cancellationToken)
    {
        // Serialized via the generated IdempotencyConflict DTO which carries
        // [JsonPropertyName] attributes that produce the snake_case wire shape:
        //   { "idempotency_key": "...", "existing_operation_id": "...", "message": "..." }
        var body = new IdempotencyConflict
        {
            IdempotencyKey = ex.IdempotencyKey,
            ExistingOperationId = ex.ExistingOperationId,
            Message = ex.Message,
        };

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        httpContext.Response.ContentType = "application/json";

        // System.Text.Json honours [JsonPropertyName] attributes, so the output is snake_case.
        await httpContext.Response.WriteAsJsonAsync(body, cancellationToken: cancellationToken);
    }
}
