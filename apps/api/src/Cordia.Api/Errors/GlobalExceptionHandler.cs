using System.Text.Json;
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
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

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

            case InvalidStateTransitionException ex:
                logger.LogWarning(ex, "Invalid state transition: {From} -> {To}", ex.From, ex.To);
                await WriteProblemAsync(httpContext, StatusCodes.Status409Conflict,
                    "Invalid state transition", ex.Message, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
        };

        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/problem+json";
        await httpContext.Response.WriteAsJsonAsync(problem, _jsonOptions, cancellationToken: cancellationToken);
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
