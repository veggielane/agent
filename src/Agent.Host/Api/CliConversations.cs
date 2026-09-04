using System.Collections.Concurrent;
using Agent.Core.Channels;
using Agent.Core.Conversations;
using Agent.Core.Events;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Agent.Host.Api;

/// <summary>In-memory history for API/CLI conversations so a REPL keeps context across requests.</summary>
public sealed class CliConversationStore : IConversationContextProvider
{
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _histories = new(StringComparer.Ordinal);
    private readonly IOptionsMonitor<ApiOptions> _options;

    public CliConversationStore(IOptionsMonitor<ApiOptions> options) => _options = options;

    public Channel Channel => Channel.Cli;

    public IReadOnlyList<ChatMessage> Get(string conversationId)
        => _histories.TryGetValue(conversationId, out var list) ? list.ToArray() : [];

    public void Append(string conversationId, string userText, string assistantText, string? userName)
    {
        var list = _histories.GetOrAdd(conversationId, _ => []);
        lock (list)
        {
            list.Add(new ChatMessage(ChatRole.User, userText) { AuthorName = userName });
            list.Add(new ChatMessage(ChatRole.Assistant, assistantText));
            var max = Math.Max(2, _options.CurrentValue.ConversationHistory);
            if (list.Count > max)
            {
                list.RemoveRange(0, list.Count - max);
            }
        }
    }

    public void Clear(string conversationId) => _histories.TryRemove(conversationId, out _);

    public Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(InboundEvent evt, int maxMessages, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ChatMessage>>(Get(evt.ConversationId).TakeLast(maxMessages).ToList());
}
