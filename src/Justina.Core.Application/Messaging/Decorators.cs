using System.Diagnostics;
using System.Text.Json;
using Justina.Core.Application.Abstractions;
using Justina.Core.Domain.Results;
using Microsoft.Extensions.Logging;

namespace Justina.Core.Application.Messaging;

/// <summary>
/// Structured logging around every command. Logs identifiers and outcomes only — never credentials,
/// never full receipt content (§40).
/// </summary>
public sealed class LoggingCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    ILogger<LoggingCommandDecorator<TCommand, TResult>> logger)
    : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken)
    {
        var commandType = typeof(TCommand).Name;
        var stopwatch = Stopwatch.StartNew();

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = command.Context.CorrelationId.Value,
            ["ConversationId"] = command.Context.ConversationId,
            ["Channel"] = command.Context.Channel.ToString(),
            ["CommandType"] = commandType,
        });

        var result = await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        if (result.IsSuccess)
        {
            logger.LogInformation(
                "Command {CommandType} succeeded in {DurationMs}ms",
                commandType,
                stopwatch.ElapsedMilliseconds);
        }
        else
        {
            logger.LogWarning(
                "Command {CommandType} refused with {ErrorCode} in {DurationMs}ms",
                commandType,
                result.Error.Code,
                stopwatch.ElapsedMilliseconds);
        }

        return result;
    }
}

/// <summary>
/// Enforces the declared capability before the handler runs. Placed outside validation so an
/// unauthorized caller learns nothing about the shape of the request they were refused.
/// </summary>
public sealed class AuthorizationCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner)
    : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken)
    {
        if (command is IRequireCapability requirement)
        {
            var user = command.Context.User;

            if (!user.IsAuthenticated || !user.Has(requirement.RequiredCapability))
            {
                return Task.FromResult(Result.Failure<TResult>(
                    ErrorCodes.Unauthorized,
                    "You are not authorized to perform this action."));
            }
        }

        return inner.HandleAsync(command, cancellationToken);
    }
}

public sealed class ValidationCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IValidator<TCommand>? validator)
    : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken)
    {
        if (validator is not null)
        {
            var validation = validator.Validate(command);

            if (validation.IsFailure)
            {
                return Task.FromResult(Result.Failure<TResult>(validation.Error));
            }
        }

        return inner.HandleAsync(command, cancellationToken);
    }
}

/// <summary>
/// Replays the first result for a repeated command key instead of executing again (§33).
/// Only successes are stored: a transient failure must remain retryable.
/// </summary>
public sealed class IdempotencyCommandDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IIdempotencyStore store,
    ILogger<IdempotencyCommandDecorator<TCommand, TResult>> logger)
    : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<TResult>> HandleAsync(TCommand command, CancellationToken cancellationToken)
    {
        if (command is not IIdempotentCommand idempotent)
        {
            return await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);
        }

        var commandType = typeof(TCommand).Name;
        var stored = await store.TryGetResultAsync(idempotent.IdempotencyKey, commandType, cancellationToken)
            .ConfigureAwait(false);

        if (stored is not null)
        {
            logger.LogInformation("Replaying stored result for {CommandType}", commandType);
            var replayed = JsonSerializer.Deserialize<TResult>(stored, SerializerOptions);

            if (replayed is not null)
            {
                return Result.Success(replayed);
            }
        }

        var result = await inner.HandleAsync(command, cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            await store.StoreResultAsync(
                    idempotent.IdempotencyKey,
                    commandType,
                    JsonSerializer.Serialize(result.Value, SerializerOptions),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }
}
