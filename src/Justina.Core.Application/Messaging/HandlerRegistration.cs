using Justina.Core.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Justina.Core.Application.Messaging;

/// <summary>
/// Explicit decorator wiring. Reading this method tells you the exact pipeline a command goes through —
/// which is the reason for hand-rolling it rather than taking a mediator dependency (§14).
/// </summary>
public static class HandlerRegistration
{
    /// <summary>
    /// Pipeline, outermost first:
    /// Logging → Authorization → Validation → Idempotency → handler.
    /// Authorization sits outside validation so a refused caller learns nothing about the request shape.
    /// </summary>
    public static IServiceCollection AddCommandHandler<TCommand, TResult, THandler>(this IServiceCollection services)
        where TCommand : class, ICommand<TResult>
        where THandler : class, ICommandHandler<TCommand, TResult>
    {
        services.AddScoped<THandler>();

        services.AddScoped<ICommandHandler<TCommand, TResult>>(provider =>
        {
            ICommandHandler<TCommand, TResult> handler = provider.GetRequiredService<THandler>();

            handler = new IdempotencyCommandDecorator<TCommand, TResult>(
                handler,
                provider.GetRequiredService<IIdempotencyStore>(),
                provider.GetRequiredService<ILogger<IdempotencyCommandDecorator<TCommand, TResult>>>());

            handler = new ValidationCommandDecorator<TCommand, TResult>(
                handler,
                provider.GetService<IValidator<TCommand>>());

            handler = new AuthorizationCommandDecorator<TCommand, TResult>(handler);

            handler = new LoggingCommandDecorator<TCommand, TResult>(
                handler,
                provider.GetRequiredService<ILogger<LoggingCommandDecorator<TCommand, TResult>>>());

            return handler;
        });

        return services;
    }

    public static IServiceCollection AddQueryHandler<TQuery, TResult, THandler>(this IServiceCollection services)
        where TQuery : class, IQuery<TResult>
        where THandler : class, IQueryHandler<TQuery, TResult>
    {
        services.AddScoped<THandler>();

        services.AddScoped<IQueryHandler<TQuery, TResult>>(provider =>
            new AuthorizationQueryDecorator<TQuery, TResult>(provider.GetRequiredService<THandler>()));

        return services;
    }
}
