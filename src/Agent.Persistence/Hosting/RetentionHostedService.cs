using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Persistence.Hosting;

/// <summary>Runs <see cref="RetentionPurger"/> shortly after startup and then every <see cref="PersistenceOptions.RetentionIntervalHours"/>.</summary>
public sealed class RetentionHostedService : BackgroundService
{
    private readonly RetentionPurger _purger;
    private readonly IOptionsMonitor<PersistenceOptions> _options;
    private readonly ILogger<RetentionHostedService> _logger;
    private readonly TimeProvider _time;

    public RetentionHostedService(
        RetentionPurger purger,
        IOptionsMonitor<PersistenceOptions> options,
        ILogger<RetentionHostedService> logger,
        TimeProvider timeProvider)
    {
        _purger = purger;
        _options = options;
        _logger = logger;
        _time = timeProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await DelayAsync(TimeSpan.FromSeconds(_options.CurrentValue.RetentionStartupDelaySeconds), stoppingToken).ConfigureAwait(false))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _options.CurrentValue;
            try
            {
                var cutoff = _time.GetUtcNow().AddDays(-options.RetentionDays);
                await _purger.PurgeAsync(cutoff, options.RetentionBatchSize, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retention run failed; next attempt in {Hours} h", options.RetentionIntervalHours);
            }

            if (!await DelayAsync(TimeSpan.FromHours(options.RetentionIntervalHours), stoppingToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
