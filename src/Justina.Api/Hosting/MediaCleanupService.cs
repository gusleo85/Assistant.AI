using Justina.Core.Application.Abstractions;
using Justina.Core.Infrastructure.Documents;
using Microsoft.Extensions.Options;

namespace Justina.Api.Hosting;

/// <summary>
/// Deletes downloaded media past its retention window. Untrusted user documents should not sit on disk
/// longer than the workflow needs them (§38).
/// </summary>
public sealed class MediaCleanupService(
    IMediaStore mediaStore,
    IOptions<DocumentProcessingOptions> options,
    ILogger<MediaCleanupService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var deleted = await mediaStore
                    .DeleteExpiredAsync(options.Value.MediaRetention, stoppingToken)
                    .ConfigureAwait(false);

                if (deleted > 0)
                {
                    logger.LogInformation("Removed {DeletedCount} expired media file(s)", deleted);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                // Cleanup must never take the service down.
                logger.LogError(exception, "Media cleanup pass failed");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
