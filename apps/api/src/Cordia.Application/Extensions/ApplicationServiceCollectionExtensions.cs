using Cordia.Application.Behaviours;
using Cordia.Application.Messaging;
using Cordia.Domain.Interfaces;
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
            // Pipeline order: 1) Validation → 2) Inbox dedup → 3) Handler.
            // Validation runs first so structurally invalid queue-driven requests are rejected
            // before the inbox transaction is opened.
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationPipelineBehavior<,>));
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(InboxDeduplicationBehavior<,>));
        });
        services.AddValidatorsFromAssembly(assembly);

        // Consumer dispatch seam (§3, OD-1): registry is a singleton catalog written once at startup;
        // dispatcher is scoped because IMediator is scoped.
        services.AddSingleton<ConsumerRegistry>();
        services.AddScoped<IQueueConsumerDispatcher, MediatRQueueConsumerDispatcher>();

        return services;
    }
}
