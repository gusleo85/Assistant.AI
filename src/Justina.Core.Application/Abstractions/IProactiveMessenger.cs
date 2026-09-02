using Justina.Core.Domain.Messaging;
using Justina.Core.Domain.Results;

namespace Justina.Core.Application.Abstractions;

/// <summary>
/// Who a proactive message goes to, in the channel's own terms.
///
/// <see cref="DisplayName"/> is carried so a message can greet someone by name. It is optional: a
/// principal seeded without one is addressed impersonally rather than as "Hello ,".
/// </summary>
public sealed record ChannelRecipient(ChannelKind Channel, string UserId, string? DisplayName = null);

/// <summary>
/// Sends a message to someone who did not just write to us.
///
/// Every other outbound message Justina produces is a reply: the person spoke, the agent answered, and
/// the gateway already knows which conversation the answer belongs to. This is the opposite — a system
/// decided there was something worth saying, and the recipient is not expecting it.
///
/// That asymmetry is why it is a separate abstraction rather than a flag on the reply path. An
/// assistant that can message people unprompted can also spam them, and Telegram will block a bot that
/// does, so this route stays narrow, deliberate, and easy to audit.
/// </summary>
public interface IProactiveMessenger
{
    /// <returns>
    /// Failure is a <see cref="Result"/> rather than an exception: the message not arriving is an
    /// ordinary outcome the caller must decide what to do about, not a defect.
    /// </returns>
    Task<Result> SendAsync(ChannelRecipient recipient, string message, CancellationToken cancellationToken);
}
