using Agent.Core.Events;
using Agent.Core.Pipeline;
using Agent.Core.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agent.Core.Tests.Pipeline;

public sealed class InboundWorkerTests
{
    private sealed class GatedProcessor : IInboundProcessor
    {
        public List<string> Started { get; } = [];

        public List<string> Finished { get; } = [];

        public int MaxParallel { get; private set; }

        private int _current;
        private readonly Lock _lock = new();

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ProcessAsync(InboundEvent evt, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                Started.Add(evt.EventId);
                _current++;
                MaxParallel = Math.Max(MaxParallel, _current);
            }

            await Gate.Task.WaitAsync(cancellationToken);

            lock (_lock)
            {
                _current--;
                Finished.Add(evt.EventId);
            }
        }
    }

    [Fact]
    public async Task SameConversation_RunsSequentially_DifferentConversations_RunInParallel()
    {
        var queue = new InboundQueue();
        var processor = new GatedProcessor();
        var options = new OptionsMonitorStub<PipelineOptions>(new PipelineOptions { MaxConcurrency = 4 });
        using var worker = new InboundWorker(queue, processor, options, NullLogger<InboundWorker>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await worker.StartAsync(cts.Token);

        var host = TestHost.Create();
        await queue.EnqueueAsync(host.Event("a1", conversationId: "A", eventId: "a1"), cts.Token);
        await queue.EnqueueAsync(host.Event("a2", conversationId: "A", eventId: "a2"), cts.Token);
        await queue.EnqueueAsync(host.Event("b1", conversationId: "B", eventId: "b1"), cts.Token);

        // Wait until the worker has started what it can start (a1 and b1; a2 must wait for a1).
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (processor.Started.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, cts.Token);
        }

        Assert.Equal(2, processor.Started.Count);
        Assert.Contains("a1", processor.Started);
        Assert.Contains("b1", processor.Started);
        Assert.DoesNotContain("a2", processor.Started);

        processor.Gate.SetResult();
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (processor.Finished.Count < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, cts.Token);
        }

        Assert.Equal(3, processor.Finished.Count);
        Assert.True(processor.Finished.IndexOf("a1") < processor.Finished.IndexOf("a2"));
        Assert.Equal(2, processor.MaxParallel);

        await worker.StopAsync(CancellationToken.None);
        host.Dispose();
    }

    [Fact]
    public async Task Queue_TracksDepth()
    {
        var queue = new InboundQueue();
        using var host = TestHost.Create();
        await queue.EnqueueAsync(host.Event("x"), TestContext.Current.CancellationToken);
        Assert.Equal(1, queue.Depth);

        await foreach (var _ in queue.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            break;
        }

        Assert.Equal(0, queue.Depth);
    }

    private sealed class OptionsMonitorStub<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
