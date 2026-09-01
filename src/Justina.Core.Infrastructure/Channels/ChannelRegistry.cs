using Justina.Core.Application.Channels;
using Justina.Core.Domain.Messaging;
using Justina.Core.Domain.Results;

namespace Justina.Core.Infrastructure.Channels;

/// <summary>
/// Keeps the only <c>switch</c> on <see cref="ChannelKind"/> in one place. Adding a channel means
/// registering two adapters, not editing domain code (§35).
/// </summary>
public sealed class ChannelRegistry : IChannelRegistry
{
    private readonly Dictionary<ChannelKind, IChannelMediaDownloader> _downloaders;
    private readonly Dictionary<ChannelKind, IChannelResponder> _responders;

    public ChannelRegistry(
        IEnumerable<IChannelMediaDownloader> downloaders,
        IEnumerable<IChannelResponder> responders)
    {
        _downloaders = downloaders.ToDictionary(d => d.Channel);
        _responders = responders.ToDictionary(r => r.Channel);
    }

    public Result<IChannelMediaDownloader> GetDownloader(ChannelKind channel) =>
        _downloaders.TryGetValue(channel, out var downloader)
            ? Result.Success(downloader)
            : Result.Failure<IChannelMediaDownloader>(
                ErrorCodes.NotAvailable,
                $"The {channel} channel is not configured.");

    public Result<IChannelResponder> GetResponder(ChannelKind channel) =>
        _responders.TryGetValue(channel, out var responder)
            ? Result.Success(responder)
            : Result.Failure<IChannelResponder>(
                ErrorCodes.NotAvailable,
                $"The {channel} channel is not configured.");
}
