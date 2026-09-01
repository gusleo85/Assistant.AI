using Justina.Core.Domain.Results;

namespace Justina.Core.Application.Abstractions;

public sealed record StoredMedia(string MediaId, byte[] Content, string MimeType, string? FileName);

/// <summary>
/// Short-lived storage for untrusted user media, so a retried or resumed command does not have to
/// re-download from the channel. Content lives outside any web root and is TTL-cleaned (§38).
/// </summary>
public interface IMediaStore
{
    Task<Result> SaveAsync(StoredMedia media, CancellationToken cancellationToken);

    Task<Result<StoredMedia>> GetAsync(string mediaId, CancellationToken cancellationToken);

    Task<int> DeleteExpiredAsync(TimeSpan retention, CancellationToken cancellationToken);
}
