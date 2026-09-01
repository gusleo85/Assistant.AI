using Justina.Core.Domain.Results;

namespace Justina.Core.Application.Channels;

/// <summary>
/// Reads media the AI gateway has already downloaded and staged on disk.
///
/// The original design had C# fetch media from the channel itself, which is why
/// <see cref="IChannelMediaDownloader"/> exists. OpenClaw does not leave that option open: it downloads
/// an inbound attachment before the agent runs, stages it under its workspace, and hands the agent a
/// path — there is no channel media id left to fetch by.
///
/// So the download step moves out of C#, and nothing else does. Everything the pipeline actually
/// guarantees — magic-byte sniffing, size and page caps, PDF parsing, multi-receipt detection,
/// the controlled extraction schema — still runs against the bytes this returns (§24).
///
/// The path is supplied by a language model, so it is treated as hostile input: see the implementation's
/// containment rules.
/// </summary>
public interface IStagedMediaReader
{
    /// <summary>Whether a staging root is configured at all. False means staged paths are refused.</summary>
    bool IsConfigured { get; }

    Task<Result<DownloadedMedia>> ReadAsync(string stagedPath, CancellationToken cancellationToken);
}
