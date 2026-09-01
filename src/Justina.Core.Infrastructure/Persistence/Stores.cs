using System.Text.Json;
using Justina.Core.Application.Abstractions;
using Justina.Core.Domain.Messaging;
using Justina.Core.Domain.Results;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Justina.Core.Infrastructure.Persistence;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// Turns SQL Server's two "someone got there first" signals into a typed conflict the agent can relay,
/// instead of letting an exception escape into the tool response (§22).
/// </summary>
public sealed class EfUnitOfWork(JustinaDbContext context, ILogger<EfUnitOfWork> logger) : IUnitOfWork
{
    private const int UniqueIndexViolation = 2601;
    private const int UniqueConstraintViolation = 2627;

    public async Task<Result> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // Name the entities and their concurrency tokens. "0 rows affected" has two very different
            // causes — a genuine concurrent write, or a token the provider never populated after insert —
            // and without this the log cannot tell them apart.
            foreach (var entry in exception.Entries)
            {
                var token = entry.Metadata
                    .GetProperties()
                    .FirstOrDefault(p => p.IsConcurrencyToken);

                var value = token is null ? null : entry.CurrentValues[token];

                logger.LogWarning(
                    "Concurrency conflict on {EntityType} (state {EntityState}); token {TokenName} = {TokenValue}",
                    entry.Metadata.Name,
                    entry.State,
                    token?.Name ?? "(none)",
                    value is byte[] bytes ? Convert.ToHexStringLower(bytes) : value?.ToString() ?? "(null)");
            }

            logger.LogWarning(exception, "Optimistic concurrency conflict while saving");

            return Result.Failure(
                ErrorCodes.Conflict,
                "Someone else changed this at the same time. Please check the current state and try again.");
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is SqlException { Number: UniqueIndexViolation or UniqueConstraintViolation })
        {
            logger.LogWarning(exception, "Uniqueness violation while saving");

            return Result.Failure(ErrorCodes.Conflict, "That operation has already been recorded.");
        }
    }
}

public sealed class SqlServerConversationStateStore(JustinaDbContext context, IClock clock)
    : IConversationStateStore
{
    public async Task<ConversationState?> GetAsync(
        ChannelKind channel,
        string externalConversationId,
        CancellationToken cancellationToken)
    {
        var record = await context.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.Channel == channel && c.ExternalConversationId == externalConversationId,
                cancellationToken)
            .ConfigureAwait(false);

        return record is null ? null : Map(record);
    }

    public async Task<ConversationState> EnsureAsync(
        ChannelKind channel,
        string externalConversationId,
        string userId,
        CancellationToken cancellationToken)
    {
        var record = await context.Conversations
            .FirstOrDefaultAsync(
                c => c.Channel == channel && c.ExternalConversationId == externalConversationId,
                cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            record = new ConversationRecord
            {
                Id = Guid.NewGuid(),
                Channel = channel,
                ExternalConversationId = externalConversationId,
                UserId = userId,
                UpdatedAtUtc = clock.UtcNow,
            };

            context.Conversations.Add(record);
        }

        return Map(record);
    }

    public async Task SetActiveWorkflowAsync(
        Guid conversationId,
        string? workflow,
        Guid? activeEntityId,
        CancellationToken cancellationToken)
    {
        var record = await context.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            return;
        }

        record.ActiveWorkflow = workflow;
        record.ActiveEntityId = activeEntityId;
        record.UpdatedAtUtc = clock.UtcNow;
    }

    private static ConversationState Map(ConversationRecord record) =>
        new(
            record.Id,
            record.Channel,
            record.ExternalConversationId,
            record.UserId,
            record.ActiveWorkflow,
            record.ActiveEntityId,
            record.UpdatedAtUtc);
}

public sealed class SqlServerIdempotencyStore(JustinaDbContext context, IClock clock) : IIdempotencyStore
{
    public async Task<string?> TryGetResultAsync(string key, string commandType, CancellationToken cancellationToken)
    {
        var record = await context.IdempotencyKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.KeyValue == key && k.CommandType == commandType, cancellationToken)
            .ConfigureAwait(false);

        return record?.ResultJson;
    }

    public async Task StoreResultAsync(
        string key,
        string commandType,
        string resultJson,
        CancellationToken cancellationToken)
    {
        context.IdempotencyKeys.Add(new IdempotencyKeyRecord
        {
            KeyValue = key,
            CommandType = commandType,
            ResultJson = resultJson,
            CreatedAtUtc = clock.UtcNow,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A concurrent execution stored the same key first. Its result stands; ours is discarded.
            context.ChangeTracker.Clear();
        }
    }
}

public sealed class SqlServerInboundMessageDeduplicator(JustinaDbContext context, IClock clock)
    : IInboundMessageDeduplicator
{
    public async Task<bool> TryRegisterAsync(
        ChannelKind channel,
        string messageId,
        CancellationToken cancellationToken)
    {
        var exists = await context.InboundMessages
            .AsNoTracking()
            .AnyAsync(m => m.Channel == channel && m.MessageId == messageId, cancellationToken)
            .ConfigureAwait(false);

        if (exists)
        {
            return false;
        }

        context.InboundMessages.Add(new InboundMessageRecord
        {
            Channel = channel,
            MessageId = messageId,
            ReceivedAtUtc = clock.UtcNow,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            // The primary key rejected a concurrent duplicate: the other caller owns this message.
            context.ChangeTracker.Clear();
            return false;
        }
    }
}

/// <summary>
/// Resolves a channel identity to capabilities. Unknown users get <see cref="UserContext.Anonymous"/>,
/// which holds nothing — an unmapped user cannot act, whatever the conversation says (§34).
/// </summary>
public sealed class AuthorizationService(JustinaDbContext context)
    : IAuthorizationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<Domain.Identity.UserContext> ResolveAsync(
        ChannelKind channel,
        string userId,
        CancellationToken cancellationToken)
    {
        var principal = await context.Principals
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Channel == channel && p.UserId == userId, cancellationToken)
            .ConfigureAwait(false);

        if (principal is null)
        {
            return Domain.Identity.UserContext.Anonymous(channel, userId);
        }

        var capabilities = Deserialize(principal.CapabilitiesJson);

        return new Domain.Identity.UserContext(
            principal.Id,
            channel,
            principal.UserId,
            principal.DisplayName,
            capabilities);
    }

    private static IReadOnlyCollection<string> Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
