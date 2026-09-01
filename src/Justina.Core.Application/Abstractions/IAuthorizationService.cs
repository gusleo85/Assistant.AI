using Justina.Core.Domain.Identity;
using Justina.Core.Domain.Messaging;

namespace Justina.Core.Application.Abstractions;

/// <summary>
/// Resolves a channel identity to a Justina principal and answers capability questions.
/// Deterministic and C#-owned: the LLM is never the final authority (§34).
/// </summary>
public interface IAuthorizationService
{
    Task<UserContext> ResolveAsync(ChannelKind channel, string userId, CancellationToken cancellationToken);
}
