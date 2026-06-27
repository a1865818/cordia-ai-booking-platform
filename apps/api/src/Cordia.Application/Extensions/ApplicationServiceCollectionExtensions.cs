using Cordia.Application.Behaviours;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Cordia.Application.Extensions;

/// <summary>Registers the Application layer (MediatR handlers, validators).</summary>
public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(ApplicationServiceCollectionExtensions).Assembly;

        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(assembly);
            // Run FluentValidation validators before every MediatR handler so that
            // structural failures (missing required fields, cross-field binding) throw
            // ValidationException → HTTP 400 before the handler can NRE on null properties.
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationPipelineBehavior<,>));
        });
        services.AddValidatorsFromAssembly(assembly);

        return services;
    }
}
