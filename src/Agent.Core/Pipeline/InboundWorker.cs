using System.Collections.Concurrent;
using Agent.Core.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Core.Pipeline;

/// <summary>
/// Drains the inbound queue. Events for the same conversation run one at a time (replies never interleave);
/// different conversations run in parallel up to <see cref="PipelineOptions.MaxConcurrency"/>.
/// </summary>
public sealed class InboundWorker : BackgroundService
{
    private readonly IInboundQueue _queue;
    private readonly IInboundProcessor _processor;
    private readonly IOptionsMonitor<PipelineOptions> _options;
    private readonly ILogger<InboundWorker> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _conversationLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();

    public InboundWorker(IInboundQueue queue, IInboundProcessor processor, IOptionsMonitor<PipelineOptions> options, ILogger<InboundWorker> logger)
    {
        _queue = queue;
        _processor = processor;
        _options = options;
        _logger = logger;
    }

    public int InFlight => _inFlight.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var global = new SemaphoreSlim(Math.Max(1, _options.CurrentValue.MaxConcurrency));
        _logger.LogInformation("Inbound worker started (max concurrency {Max})", _options.CurrentValue.MaxConcurrency);

        try
        {
            await foreach (var evt in _queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await global.WaitAsync(stoppingToken).ConfigureAwait(false);
                var task = RunAsync(evt, global, stoppingToken);
                _inFlight[task] = 0;
                _ = task.ContinueWith(t => _inFlight.TryRemove(t, out _), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await Task.WhenAll(_inFlight.Keys).ConfigureAwait(false);
    }

    private async Task RunAsync(InboundEvent evt, SemaphoreSlim global, CancellationToken ct)
    {
        var conversationLock = _conversationLocks.GetOrAdd(evt.ConversationId, _ => new SemaphoreSlim(1, 1));
        try
        {
            await conversationLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _processor.ProcessAsync(evt, ct).ConfigureAwait(false);
            }
            finally
            {
                conversationLock.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error processing {Channel}:{EventId}", evt.Channel, evt.EventId);
        }
        finally
        {
            global.Release();
        }
    }
}
