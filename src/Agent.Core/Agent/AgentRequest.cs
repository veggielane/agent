using Agent.Core.Authorization;
using Agent.Core.Channels;
using Microsoft.Extensions.AI;

namespace Agent.Core.Agent;

public sealed record AgentRequest
{
    public required CallerIdentity Caller { get; init; }

    public required Channel Channel { get; init; }

    public required string ConversationId { get; init; }

    public required string Text { get; init; }

    public IReadOnlyList<ChatMessage> History { get; init; } = [];

    /// <summary>Explicit model name (CLI --model, YAML command). Subject to <c>Llm.AllowClientModelOverride</c> when it came from a client.</summary>
    public string? ModelOverride { get; init; }

    public bool ModelOverrideFromClient { get; init; }

    /// <summary>Restrict tools to these names (YAML commands). Null = everything the caller may use.</summary>
    public IReadOnlyCollection<string>? ToolFilter { get; init; }

    public string? SystemPromptOverride { get; init; }

    /// <summary>Appended to the system prompt (repo guidance, command-specific rules).</summary>
    public string? ExtraInstructions { get; init; }

    public bool DisableTools { get; init; }
}

public sealed record AgentResponse(string Markdown, UsageDetails? Usage, IReadOnlyList<string> ToolsUsed, string Model);

public interface IAgent
{
    Task<AgentResponse> AnswerAsync(AgentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Streams text chunks; tool calls happen inside the stream.</summary>
    IAsyncEnumerable<string> StreamAsync(AgentRequest request, CancellationToken cancellationToken = default);
}
