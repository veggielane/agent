using System.Runtime.CompilerServices;
using Agent.Core.Conversations;
using Agent.Core.Llm;
using Agent.Core.Prompts;
using Agent.Core.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Core.Agent;

/// <summary>The answer loop: system prompt + history + question → model with tools → markdown.</summary>
public sealed class AgentService : IAgent
{
    private readonly IChatClientFactory _clients;
    private readonly IToolRegistry _tools;
    private readonly IPromptProvider _prompts;
    private readonly IConversationSettings _settings;
    private readonly IOptionsMonitor<LlmOptions> _options;
    private readonly ILogger<AgentService> _logger;

    public AgentService(
        IChatClientFactory clients,
        IToolRegistry tools,
        IPromptProvider prompts,
        IConversationSettings settings,
        IOptionsMonitor<LlmOptions> options,
        ILogger<AgentService> logger)
    {
        _clients = clients;
        _tools = tools;
        _prompts = prompts;
        _settings = settings;
        _options = options;
        _logger = logger;
    }

    public async Task<AgentResponse> AnswerAsync(AgentRequest request, CancellationToken cancellationToken = default)
    {
        var (client, options, messages, model) = Prepare(request);
        var response = await client.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        var toolsUsed = response.Messages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(c => c.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var text = response.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = "_(The model returned no text.)_";
        }

        _logger.LogInformation(
            "Answered {Conversation} on {Channel} with {Model}; tools=[{Tools}] tokens={Tokens}",
            request.ConversationId,
            request.Channel,
            model,
            string.Join(",", toolsUsed),
            response.Usage?.TotalTokenCount);

        return new AgentResponse(text.Trim(), response.Usage, toolsUsed, model);
    }

    public async IAsyncEnumerable<string> StreamAsync(AgentRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (client, options, messages, _) = Prepare(request);
        await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            var text = update.Text;
            if (!string.IsNullOrEmpty(text))
            {
                yield return text;
            }
        }
    }

    private (IChatClient Client, ChatOptions Options, List<ChatMessage> Messages, string Model) Prepare(AgentRequest request)
    {
        var llm = _options.CurrentValue;

        string? explicitModel = null;
        if (!string.IsNullOrWhiteSpace(request.ModelOverride) && (!request.ModelOverrideFromClient || llm.AllowClientModelOverride))
        {
            explicitModel = request.ModelOverride;
        }

        explicitModel ??= _settings.GetModel(request.ConversationId) ?? _settings.GlobalModel;

        var overrideKey = $"{request.Channel}.Answer";
        var model = _clients.ResolveModel(ModelPurpose.Answer, overrideKey, explicitModel);
        var client = _clients.Create(ModelPurpose.Answer, overrideKey, explicitModel);

        var system = request.SystemPromptOverride ?? _prompts.GetSystemPrompt(request.Channel);
        system += $"\n\nYou are talking to {request.Caller.DisplayName} (roles: {string.Join(", ", request.Caller.Roles)}) via {request.Channel}.";
        if (!string.IsNullOrWhiteSpace(request.ExtraInstructions))
        {
            system += "\n\n" + request.ExtraInstructions;
        }

        var messages = new List<ChatMessage>(request.History.Count + 2) { new(ChatRole.System, system) };
        messages.AddRange(request.History);
        messages.Add(new ChatMessage(ChatRole.User, request.Text));

        var options = new ChatOptions
        {
            ModelId = model,
            MaxOutputTokens = llm.MaxOutputTokens,
            Temperature = llm.Temperature,
        };

        if (!request.DisableTools)
        {
            var tools = _tools.GetTools(request.Caller, ToolScope.Answer, request.Channel, request.ToolFilter);
            if (tools.Count > 0)
            {
                options.Tools = tools.Select(t => (AITool)t.Function).ToList();
            }
        }

        return (client, options, messages, model);
    }
}
