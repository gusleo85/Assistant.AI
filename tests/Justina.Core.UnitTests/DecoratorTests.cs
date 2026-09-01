using Justina.Core.Application.Abstractions;
using Justina.Core.Application.Messaging;
using Justina.Core.Domain.Identity;
using Justina.Core.Domain.Messaging;
using Justina.Core.Domain.Observability;
using Justina.Core.Domain.Results;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Justina.Core.UnitTests;

public class DecoratorTests
{
    private static RequestContext ContextFor(params string[] capabilities) =>
        new(
            new UserContext(Guid.NewGuid(), ChannelKind.Telegram, "user-1", "Test User", capabilities),
            ChannelKind.Telegram,
            "conversation-1",
            CorrelationId.New());

    private static RequestContext AnonymousContext() =>
        new(
            UserContext.Anonymous(ChannelKind.Telegram, "user-1"),
            ChannelKind.Telegram,
            "conversation-1",
            CorrelationId.New());

    private sealed record ProtectedCommand(RequestContext Context) : ICommand<string>, IRequireCapability
    {
        public string RequiredCapability => Capabilities.ExpenseSubmit;
    }

    private sealed record KeyedCommand(RequestContext Context, string IdempotencyKey)
        : ICommand<string>, IIdempotentCommand;

    private sealed class CountingHandler<TCommand> : ICommandHandler<TCommand, string>
        where TCommand : ICommand<string>
    {
        public int Calls { get; private set; }

        public Result<string> NextResult { get; set; } = Result.Success("done");

        public Task<Result<string>> HandleAsync(TCommand command, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(NextResult);
        }
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_refused_and_the_handler_never_runs()
    {
        var handler = new CountingHandler<ProtectedCommand>();
        var decorator = new AuthorizationCommandDecorator<ProtectedCommand, string>(handler);

        var result = await decorator.HandleAsync(new ProtectedCommand(AnonymousContext()), default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.Unauthorized);
        handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_caller_without_the_capability_is_refused()
    {
        var handler = new CountingHandler<ProtectedCommand>();
        var decorator = new AuthorizationCommandDecorator<ProtectedCommand, string>(handler);

        var result = await decorator.HandleAsync(new ProtectedCommand(ContextFor(Capabilities.ExpenseRead)), default);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(ErrorCodes.Unauthorized);
        handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_caller_with_the_capability_reaches_the_handler()
    {
        var handler = new CountingHandler<ProtectedCommand>();
        var decorator = new AuthorizationCommandDecorator<ProtectedCommand, string>(handler);

        var result = await decorator.HandleAsync(new ProtectedCommand(ContextFor(Capabilities.ExpenseSubmit)), default);

        result.IsSuccess.ShouldBeTrue();
        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_replayed_command_returns_the_stored_result_without_running_again()
    {
        var store = Substitute.For<IIdempotencyStore>();
        store.TryGetResultAsync("key-1", nameof(KeyedCommand), Arg.Any<CancellationToken>())
            .Returns("\"first-answer\"");

        var handler = new CountingHandler<KeyedCommand>();
        var decorator = new IdempotencyCommandDecorator<KeyedCommand, string>(
            handler,
            store,
            NullLogger<IdempotencyCommandDecorator<KeyedCommand, string>>.Instance);

        var result = await decorator.HandleAsync(new KeyedCommand(ContextFor(), "key-1"), default);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("first-answer");
        handler.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_first_execution_stores_its_result()
    {
        var store = Substitute.For<IIdempotencyStore>();
        store.TryGetResultAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var handler = new CountingHandler<KeyedCommand>();
        var decorator = new IdempotencyCommandDecorator<KeyedCommand, string>(
            handler,
            store,
            NullLogger<IdempotencyCommandDecorator<KeyedCommand, string>>.Instance);

        await decorator.HandleAsync(new KeyedCommand(ContextFor(), "key-2"), default);

        handler.Calls.ShouldBe(1);
        await store.Received(1).StoreResultAsync("key-2", nameof(KeyedCommand), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A transient failure must stay retryable, so failures are never cached.</summary>
    [Fact]
    public async Task A_failed_command_is_not_stored()
    {
        var store = Substitute.For<IIdempotencyStore>();
        store.TryGetResultAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var handler = new CountingHandler<KeyedCommand>
        {
            NextResult = Result.Failure<string>(ErrorCodes.ExternalApiFailed, "timeout"),
        };

        var decorator = new IdempotencyCommandDecorator<KeyedCommand, string>(
            handler,
            store,
            NullLogger<IdempotencyCommandDecorator<KeyedCommand, string>>.Instance);

        var result = await decorator.HandleAsync(new KeyedCommand(ContextFor(), "key-3"), default);

        result.IsFailure.ShouldBeTrue();
        await store.DidNotReceiveWithAnyArgs().StoreResultAsync(default!, default!, default!, default);
    }
}
