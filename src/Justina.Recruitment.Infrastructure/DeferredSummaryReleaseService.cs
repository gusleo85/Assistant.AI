using Justina.Recruitment.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Justina.Recruitment.Infrastructure;

/// <summary>
/// Sends the candidate summaries that were held back, once their recipient is free.
///
/// A sweep rather than a callback from the expense flow. A callback would mean the expense side knowing
/// recruitment exists — the two domains are deliberately ignorant of each other — and anything deferred
/// while the process was restarting would be lost. A sweep survives a restart and needs no coupling.
///
/// The cost is latency: a summary held during a receipt confirmation arrives up to one interval after
/// the receipt is finished. For a message that says "when would you like to interview them", half a
/// minute is not the difference between useful and useless.
/// </summary>
public sealed class DeferredSummaryReleaseService(
    IServiceScopeFactory scopeFactory,
    ILogger<DeferredSummaryReleaseService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Enough to clear a backlog promptly, small enough that one slow gateway cannot make a sweep run
    /// long enough to overlap the next.
    /// </summary>
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var summaries = scope.ServiceProvider.GetRequiredService<CandidateSummaryService>();

                var sent = await summaries.ReleaseDeferredAsync(BatchSize, stoppingToken).ConfigureAwait(false);

                if (sent > 0)
                {
                    logger.LogInformation("Released {Count} held candidate summary(s)", sent);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A sweep that throws must not end the loop: the next one is the retry, and a summary
                // left queued forever because of one bad tick is exactly what this service exists to
                // prevent.
                logger.LogError(exception, "A candidate summary sweep failed; the next one will retry");
            }
        }
    }
}
