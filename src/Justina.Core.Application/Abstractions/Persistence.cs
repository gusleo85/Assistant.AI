using Justina.Core.Domain.Messaging;
using Justina.Core.Domain.Results;

namespace Justina.Core.Application.Abstractions;

/// <summary>Testable clock. Everything stored is UTC.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IUnitOfWork
{
    Task<Result> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Which workflow, if any, currently owns a conversation. Read by the Intent Router, which is why an
/// active workflow can dominate routing without the AI having to remember it (§18).
/// </summary>
/// <param name="Id">Justina's internal conversation id — the foreign key domain entities hang off.</param>
/// <param name="ExternalConversationId">The channel's own conversation/chat identifier.</param>
public sealed record ConversationState(
    Guid Id,
    ChannelKind Channel,
    string ExternalConversationId,
    string UserId,
    string? ActiveWorkflow,
    Guid? ActiveEntityId,
    DateTimeOffset UpdatedAtUtc);

public static class Workflows
{
    public const string ExpenseReceipt = "expense.receipt";
}

public interface IConversationStateStore
{
    Task<ConversationState?> GetAsync(
        ChannelKind channel,
        string externalConversationId,
        CancellationToken cancellationToken);

    /// <summary>Returns the conversation, creating it on first contact.</summary>
    Task<ConversationState> EnsureAsync(
        ChannelKind channel,
        string externalConversationId,
        string userId,
        CancellationToken cancellationToken);

    Task SetActiveWorkflowAsync(
        Guid conversationId,
        string? workflow,
        Guid? activeEntityId,
        CancellationToken cancellationToken);
}

/// <summary>Stores the first result for a command key so a retry replays instead of re-executing (§33).</summary>
public interface IIdempotencyStore
{
    Task<string?> TryGetResultAsync(string key, string commandType, CancellationToken cancellationToken);

    Task StoreResultAsync(string key, string commandType, string resultJson, CancellationToken cancellationToken);
}

/// <summary>Drops repeated inbound messages caused by channel/webhook retries (§33).</summary>
public interface IInboundMessageDeduplicator
{
    /// <returns><c>true</c> when this message has not been seen before and should be processed.</returns>
    Task<bool> TryRegisterAsync(ChannelKind channel, string messageId, CancellationToken cancellationToken);
}
