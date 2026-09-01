using Justina.Core.Application.Abstractions;
using Justina.Core.Application.Messaging;
using Justina.Core.Domain.Results;

namespace Justina.Core.Application.Session;

/// <summary>
/// What the Intent Router needs to route correctly (§18): who the user is, what they are allowed to do,
/// and whether a workflow already owns this conversation. C# is the source of truth for all three, so the
/// AI layer never has to hold state it could get wrong.
/// </summary>
public sealed record SessionContext(
    string Channel,
    string ConversationId,
    bool IsAuthenticated,
    string DisplayName,
    IReadOnlyCollection<string> Capabilities,
    string? ActiveWorkflow,
    Guid? ActiveEntityId);

public sealed record GetSessionContextQuery(RequestContext Context) : IQuery<SessionContext>;

public sealed class GetSessionContextQueryHandler(IConversationStateStore conversations)
    : IQueryHandler<GetSessionContextQuery, SessionContext>
{
    public async Task<Result<SessionContext>> HandleAsync(
        GetSessionContextQuery query,
        CancellationToken cancellationToken)
    {
        var conversation = await conversations
            .GetAsync(query.Context.Channel, query.Context.ConversationId, cancellationToken)
            .ConfigureAwait(false);

        var user = query.Context.User;

        return Result.Success(new SessionContext(
            query.Context.Channel.ToString(),
            query.Context.ConversationId,
            user.IsAuthenticated,
            user.DisplayName,
            user.Capabilities,
            conversation?.ActiveWorkflow,
            conversation?.ActiveEntityId));
    }
}
