using Justina.Core.Domain.Identity;
using Justina.Core.Domain.Messaging;
using Justina.Core.Domain.Observability;
using Justina.Core.Domain.Results;

namespace Justina.Core.Application.Messaging;

/// <summary>
/// Everything a handler needs to know about who is asking and under which conversation.
/// Carried explicitly rather than pulled from ambient state, so handlers stay unit-testable.
/// </summary>
public sealed record RequestContext(
    UserContext User,
    ChannelKind Channel,
    string ConversationId,
    CorrelationId CorrelationId);

/// <summary>Marker for a state-changing request (§14).</summary>
public interface ICommand<TResult>
{
    RequestContext Context { get; }
}

/// <summary>Marker for a read-only request. Query handlers must not mutate state (§14).</summary>
public interface IQuery<TResult>
{
    RequestContext Context { get; }
}

/// <summary>
/// Commands that require a capability declare it here; the authorization decorator enforces it
/// before the handler runs (§34).
/// </summary>
public interface IRequireCapability
{
    string RequiredCapability { get; }
}

/// <summary>
/// Commands that must survive retries declare a stable key; the idempotency decorator replays the
/// original result instead of executing twice (§33).
/// </summary>
public interface IIdempotentCommand
{
    string IdempotencyKey { get; }
}

public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<Result<TResult>> HandleAsync(TQuery query, CancellationToken cancellationToken);
}

/// <summary>Entry point used by the Tool API so endpoints never resolve handlers themselves.</summary>
public interface IDispatcher
{
    Task<Result<TResult>> SendAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken);

    Task<Result<TResult>> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken);
}

/// <summary>Optional per-request validation. Absent validator means "nothing to check".</summary>
public interface IValidator<in T>
{
    Result Validate(T instance);
}
