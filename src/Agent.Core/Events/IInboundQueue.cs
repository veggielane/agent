using System.Threading.Channels;

namespace Agent.Core.Events;

public interface IInboundQueue
{
    ValueTask EnqueueAsync(InboundEvent evt, CancellationToken cancellationToken = default);

    IAsyncEnumerable<InboundEvent> ReadAllAsync(CancellationToken cancellationToken = default);

    int Depth { get; }
}

public sealed class InboundQueue : IInboundQueue
{
    private readonly Channel<InboundEvent> _channel;
    private int _depth;

    public InboundQueue(int capacity = 1000)
    {
        _channel = System.Threading.Channels.Channel.CreateBounded<InboundEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public int Depth => Volatile.Read(ref _depth);

    public async ValueTask EnqueueAsync(InboundEvent evt, CancellationToken cancellationToken = default)
    {
        await _channel.Writer.WriteAsync(evt, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _depth);
    }

    public async IAsyncEnumerable<InboundEvent> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var evt in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Decrement(ref _depth);
            yield return evt;
        }
    }
}
