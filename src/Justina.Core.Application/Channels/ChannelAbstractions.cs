using Justina.Core.Domain.Messaging;
using Justina.Core.Domain.Results;

namespace Justina.Core.Application.Channels;

public sealed record DownloadedMedia(byte[] Content, string MimeType, string? FileName);

/// <summary>Split from <see cref="IChannelResponder"/> so callers depend only on what they use (ISP).</summary>
public interface IChannelMediaDownloader
{
    ChannelKind Channel { get; }

    Task<Result<DownloadedMedia>> DownloadAsync(MediaReference media, CancellationToken cancellationToken);
}

public interface IChannelResponder
{
    ChannelKind Channel { get; }

    Task<Result> SendTextAsync(string conversationId, string text, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves the adapter for a channel. Keeps <c>switch</c> statements on <see cref="ChannelKind"/>
/// out of domain and application code.
/// </summary>
public interface IChannelRegistry
{
    Result<IChannelMediaDownloader> GetDownloader(ChannelKind channel);

    Result<IChannelResponder> GetResponder(ChannelKind channel);
}
