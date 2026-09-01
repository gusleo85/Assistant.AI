using Justina.Core.Domain.Results;

namespace Justina.Core.Application.Messaging;

/// <summary>
/// Queries are authorized too, but never validated or made idempotent — they change nothing (§14).
/// </summary>
public sealed class AuthorizationQueryDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner)
    : IQueryHandler<TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    public Task<Result<TResult>> HandleAsync(TQuery query, CancellationToken cancellationToken)
    {
        if (query is IRequireCapability requirement)
        {
            var user = query.Context.User;

            if (!user.IsAuthenticated || !user.Has(requirement.RequiredCapability))
            {
                return Task.FromResult(Result.Failure<TResult>(
                    ErrorCodes.Unauthorized,
                    "You are not authorized to view this."));
            }
        }

        return inner.HandleAsync(query, cancellationToken);
    }
}
