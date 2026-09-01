using System.Collections.Concurrent;
using System.Reflection;
using Justina.Core.Domain.Results;

namespace Justina.Core.Application.Messaging;

/// <summary>
/// Resolves and invokes the registered handler for a request. Exists so Tool API endpoints depend on one
/// abstraction instead of a dozen handler interfaces; it deliberately does no pipeline work of its own —
/// cross-cutting concerns live in the decorators wired at registration time.
/// </summary>
public sealed class Dispatcher(IServiceProvider provider) : IDispatcher
{
    private static readonly ConcurrentDictionary<Type, MethodInfo> HandleMethods = new();

    public Task<Result<TResult>> SendAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return InvokeAsync<TResult>(command, typeof(ICommandHandler<,>), cancellationToken);
    }

    public Task<Result<TResult>> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return InvokeAsync<TResult>(query, typeof(IQueryHandler<,>), cancellationToken);
    }

    private Task<Result<TResult>> InvokeAsync<TResult>(
        object request,
        Type openHandlerType,
        CancellationToken cancellationToken)
    {
        var handlerType = openHandlerType.MakeGenericType(request.GetType(), typeof(TResult));
        var handler = provider.GetService(handlerType)
            ?? throw new InvalidOperationException($"No handler registered for {request.GetType().Name}.");

        var method = HandleMethods.GetOrAdd(
            handlerType,
            static type => type.GetMethod("HandleAsync")
                ?? throw new InvalidOperationException($"{type.Name} has no HandleAsync method."));

        return (Task<Result<TResult>>)method.Invoke(handler, [request, cancellationToken])!;
    }
}
