namespace Paster;

/// <summary>
/// Deletes expired uploads on a timer. This is best-effort housekeeping only:
/// App Service stops idle free-tier apps, and a recycled instance loses the timer,
/// so every lookup re-checks expiry and deletes on its own.
/// </summary>
public sealed class ExpirySweeper(TempStore store, ILogger<ExpirySweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    return;
                }

                await store.SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Sweep failed; retrying on the next tick.");
            }
        }
    }
}
