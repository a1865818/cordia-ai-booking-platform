using FluentValidation;
using MediatR;

namespace Cordia.Application.Behaviours;

/// <summary>
/// MediatR pipeline behaviour that runs FluentValidation validators registered for
/// <typeparamref name="TRequest"/> before the handler executes.
///
/// When one or more validators are registered for the request type they are all invoked
/// concurrently; any failures are collected and thrown as <see cref="ValidationException"/>.
/// That exception propagates to the API's global exception handler, which maps it to
/// HTTP 400 with a standard <c>ValidationProblemDetails</c> response. (Validators here target
/// the MediatR command, so this pipeline behaviour — not an MVC action filter — is where they run.)
///
/// If no validator is registered for the request type the behaviour is a no-op passthrough.
/// </summary>
public sealed class ValidationPipelineBehavior<TRequest, TResponse>(
    IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!validators.Any())
            return await next(cancellationToken);

        var context = new ValidationContext<TRequest>(request);

        var results = await Task.WhenAll(
            validators.Select(v => v.ValidateAsync(context, cancellationToken)));

        var failures = results
            .SelectMany(r => r.Errors)
            .Where(f => f is not null)
            .ToList();

        if (failures.Count > 0)
            throw new ValidationException(failures);

        return await next(cancellationToken);
    }
}
