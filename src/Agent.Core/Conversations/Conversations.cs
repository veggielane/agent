using System.Collections.Concurrent;
using System.Text;
using Agent.Core.Channels;
using Agent.Core.Events;
using Microsoft.Extensions.AI;

namespace Agent.Core.Conversations;

/// <summary>Loads the history behind an event: a Mattermost thread, a Jira issue's comments, a GitLab discussion.</summary>
public interface IConversationContextProvider
{
    Channel Channel { get; }

    Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(InboundEvent evt, int maxMessages, CancellationToken cancellationToken);
}

public interface IConversationContextRouter
{
    Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(InboundEvent evt, int maxMessages, CancellationToken cancellationToken);

    /// <summary>History as plain text, for prompt templates.</summary>
    Task<string> GetHistoryTextAsync(InboundEvent evt, int maxMessages, CancellationToken cancellationToken);
}

public sealed class ConversationContextRouter : IConversationContextRouter
{
    private readonly IEnumerable<IConversationContextProvider> _providers;

    public ConversationContextRouter(IEnumerable<IConversationContextProvider> providers) => _providers = providers;

    public async Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(InboundEvent evt, int maxMessages, CancellationToken cancellationToken)
    {
        var provider = _providers.FirstOrDefault(p => p.Channel == evt.Channel);
        if (provider is null)
        {
            return [];
        }

        return await provider.GetHistoryAsync(evt, maxMessages, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetHistoryTextAsync(InboundEvent evt, int maxMessages, CancellationToken cancellationToken)
    {
        var history = await GetHistoryAsync(evt, maxMessages, cancellationToken).ConfigureAwait(false);
        var sb = new StringBuilder();
        foreach (var message in history)
        {
            var who = message.AuthorName ?? (message.Role == ChatRole.Assistant ? "agent" : message.Role.Value);
            sb.Append(who).Append(": ").AppendLine(message.Text);
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>Per-conversation and global runtime settings changed by !model.</summary>
public interface IConversationSettings
{
    string? GetModel(string conversationId);

    void SetModel(string conversationId, string? model);

    string? GlobalModel { get; set; }
}

public sealed class InMemoryConversationSettings : IConversationSettings
{
    private readonly ConcurrentDictionary<string, string> _models = new(StringComparer.Ordinal);

    public string? GlobalModel { get; set; }

    public string? GetModel(string conversationId) => _models.TryGetValue(conversationId, out var m) ? m : null;

    public void SetModel(string conversationId, string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            _models.TryRemove(conversationId, out _);
        }
        else
        {
            _models[conversationId] = model;
        }
    }
}
