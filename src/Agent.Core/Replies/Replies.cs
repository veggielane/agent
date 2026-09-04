using Agent.Core.Channels;
using Agent.Core.Events;
using Agent.Core.Formatting;
using Microsoft.Extensions.Logging;

namespace Agent.Core.Replies;

public enum AckState
{
    Working,
    Done,
    Failed,
}

/// <summary>Sends replies and acknowledgements for one channel.</summary>
public interface IReplySender
{
    Channel Channel { get; }

    /// <param name="text">Already formatted for the channel.</param>
    Task SendAsync(InboundEvent source, string text, CancellationToken cancellationToken);

    /// <summary>Reaction / typing indicator / status note. Best effort; must not throw for unsupported states.</summary>
    Task AcknowledgeAsync(InboundEvent source, AckState state, string? note, CancellationToken cancellationToken);
}

public interface IReplyRouter
{
    Task SendAsync(InboundEvent source, string markdown, CancellationToken cancellationToken);

    Task AcknowledgeAsync(InboundEvent source, AckState state, string? note, CancellationToken cancellationToken);
}

public sealed class ReplyRouter : IReplyRouter
{
    private readonly IEnumerable<IReplySender> _senders;
    private readonly IFormatterRegistry _formatters;
    private readonly ILogger<ReplyRouter> _logger;

    public ReplyRouter(IEnumerable<IReplySender> senders, IFormatterRegistry formatters, ILogger<ReplyRouter> logger)
    {
        _senders = senders;
        _formatters = formatters;
        _logger = logger;
    }

    public async Task SendAsync(InboundEvent source, string markdown, CancellationToken cancellationToken)
    {
        var sender = _senders.FirstOrDefault(s => s.Channel == source.Channel);
        if (sender is null)
        {
            _logger.LogWarning("No reply sender for {Channel}; dropping reply to {Conversation}", source.Channel, source.ConversationId);
            return;
        }

        await sender.SendAsync(source, _formatters.Format(source.Channel, markdown), cancellationToken).ConfigureAwait(false);
    }

    public async Task AcknowledgeAsync(InboundEvent source, AckState state, string? note, CancellationToken cancellationToken)
    {
        var sender = _senders.FirstOrDefault(s => s.Channel == source.Channel);
        if (sender is null)
        {
            return;
        }

        try
        {
            await sender.AcknowledgeAsync(source, state, note, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Acknowledge {State} failed on {Channel}", state, source.Channel);
        }
    }
}
