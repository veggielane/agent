using System.ClientModel;
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;

namespace Agent.Core.Llm;

/// <summary>Builds <see cref="IChatClient"/>s against the OpenAI-compatible endpoint. Clients are cached per endpoint+model.</summary>
public sealed class OpenAIChatClientFactory : IChatClientFactory
{
    private readonly IOptionsMonitor<LlmOptions> _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, IChatClient> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OpenAIClient> _openAiClients = new(StringComparer.Ordinal);

    public OpenAIChatClientFactory(IOptionsMonitor<LlmOptions> options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _loggerFactory = loggerFactory;
        _options.OnChange(_ =>
        {
            _clients.Clear();
            _openAiClients.Clear();
        });
    }

    public string ResolveModel(ModelPurpose purpose, string? overrideKey = null, string? explicitModel = null)
        => _options.CurrentValue.ResolveModel(purpose, overrideKey, explicitModel);

    public IChatClient Create(ModelPurpose purpose, string? overrideKey = null, string? explicitModel = null)
    {
        var options = _options.CurrentValue;
        var model = options.ResolveModel(purpose, overrideKey, explicitModel);
        var key = $"{options.BaseUrl}|{options.ApiKey}|{model}|{options.MaxToolIterations}";

        return _clients.GetOrAdd(key, _ =>
        {
            var openAi = _openAiClients.GetOrAdd($"{options.BaseUrl}|{options.ApiKey}|{options.TimeoutSeconds}", _ =>
                new OpenAIClient(
                    new ApiKeyCredential(string.IsNullOrEmpty(options.ApiKey) ? "not-set" : options.ApiKey),
                    new OpenAIClientOptions
                    {
                        Endpoint = new Uri(options.BaseUrl),
                        NetworkTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
                    }));

            IChatClient inner = openAi.GetChatClient(model).AsIChatClient();
            return new ChatClientBuilder(inner)
                .UseFunctionInvocation(_loggerFactory, o =>
                {
                    o.MaximumIterationsPerRequest = options.MaxToolIterations;
                    o.IncludeDetailedErrors = true;
                })

                // Emits gen_ai spans and metrics. Prompts and completions stay out unless explicitly
                // enabled, because they carry ticket and repository content.
                .UseOpenTelemetry(
                    _loggerFactory,
                    Observability.AgentTelemetry.LlmActivitySourceName,
                    o => o.EnableSensitiveData = options.EnableSensitiveTelemetry)
                .Build();
        });
    }
}
