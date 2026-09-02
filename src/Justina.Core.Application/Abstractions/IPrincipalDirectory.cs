using Justina.Core.Domain.Messaging;

namespace Justina.Core.Application.Abstractions;

/// <summary>A person Justina knows on a channel: their id there, and what to call them.</summary>
public sealed record PrincipalIdentity(string UserId, string? DisplayName);

/// <summary>
/// Reads the people Justina knows about.
///
/// Principals are already how Justina decides who may do anything; this exposes the other direction —
/// given a channel, who is there to talk to. It exists so a proactive message goes to someone the system
/// has on record rather than to an id copied into a second setting, which is how a message ends up on
/// the wrong phone.
/// </summary>
public interface IPrincipalDirectory
{
    /// <returns>
    /// The channel user id to address when nobody more specific is known, or null when the channel has
    /// no principals at all. Today there is one; the caller must not assume that stays true.
    /// </returns>
    Task<PrincipalIdentity?> GetPrimaryAsync(ChannelKind channel, CancellationToken cancellationToken);
}
