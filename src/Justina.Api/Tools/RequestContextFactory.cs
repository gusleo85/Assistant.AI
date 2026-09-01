using Justina.Core.Application.Abstractions;
using Justina.Core.Application.Messaging;
using Justina.Core.Domain.Messaging;
using Justina.Core.Domain.Observability;
using Justina.Core.Domain.Results;

namespace Justina.Api.Tools;

/// <summary>
/// Turns an agent-supplied envelope into a <see cref="RequestContext"/> with a principal resolved from the
/// database. The agent cannot inflate its own permissions here: it supplies a channel identity, and the
/// capabilities attached to it come from <see cref="IAuthorizationService"/> alone (§34).
/// </summary>
public sealed class RequestContextFactory(IAuthorizationService authorization)
{
    public async Task<Result<RequestContext>> CreateAsync(
        ToolEnvelope? envelope,
        CancellationToken cancellationToken)
    {
        if (envelope is null)
        {
            return Result.Failure<RequestContext>(ErrorCodes.Validation, "The request envelope is missing.");
        }

        if (string.IsNullOrWhiteSpace(envelope.UserId) || string.IsNullOrWhiteSpace(envelope.ConversationId))
        {
            return Result.Failure<RequestContext>(
                ErrorCodes.Validation,
                "The request envelope needs a user id and a conversation id.");
        }

        if (!TryParseChannel(envelope.Channel, out var channel))
        {
            return Result.Failure<RequestContext>(
                ErrorCodes.Validation,
                $"'{envelope.Channel}' is not a supported channel.");
        }

        var user = await authorization
            .ResolveAsync(channel, envelope.UserId, cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(new RequestContext(
            user,
            channel,
            envelope.ConversationId,
            CorrelationId.From(envelope.CorrelationId)));
    }

    private static bool TryParseChannel(string? value, out ChannelKind channel)
    {
        channel = ChannelKind.Unknown;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "telegram" => Set(ChannelKind.Telegram, out channel),
            "whatsapp" => Set(ChannelKind.WhatsApp, out channel),
            _ => false,
        };

        static bool Set(ChannelKind value, out ChannelKind target)
        {
            target = value;
            return true;
        }
    }
}
