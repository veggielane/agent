using System.Runtime.CompilerServices;
using Agent.Core.Llm;
using Microsoft.Extensions.AI;

namespace Agent.Core.Tests.Support;

/// <summary>An IChatClient that returns pre-planned responses and records what it was asked.</summary>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse>> _script = new();

    public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

    public ScriptedChatClient Reply(string text, long? tokens = null)
    {
        _script.Enqueue((_, _) =>
        {
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
            if (tokens is not null)
            {
                response.Usage = new UsageDetails { InputTokenCount = tokens / 2, OutputTokenCount = tokens / 2, TotalTokenCount = tokens };
            }

            return response;
        });
        return this;
    }

    public ScriptedChatClient Reply(Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> factory)
    {
        _script.Enqueue(factory);
        return this;
    }

    public ScriptedChatClient CallTool(string name, IDictionary<string, object?>? arguments = null)
    {
        _script.Enqueue((_, _) => new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, arguments)])));
        return this;
    }

    public int Remaining => _script.Count;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Calls.Add((list, options));
        if (_script.Count == 0)
        {
            throw new InvalidOperationException("ScriptedChatClient has no responses left.");
        }

        return Task.FromResult(_script.Dequeue()(list, options));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var message in response.Messages)
        {
            yield return new ChatResponseUpdate(message.Role, message.Contents);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}

public sealed class FakeChatClientFactory : IChatClientFactory
{
    public ScriptedChatClient Client { get; } = new();

    public bool WithFunctionInvocation { get; set; } = true;

    public string Model { get; set; } = "test-model";

    public List<(ModelPurpose Purpose, string? OverrideKey, string? ExplicitModel)> Requests { get; } = [];

    public IChatClient Create(ModelPurpose purpose, string? overrideKey = null, string? explicitModel = null)
    {
        Requests.Add((purpose, overrideKey, explicitModel));
        return WithFunctionInvocation
            ? new ChatClientBuilder(Client).UseFunctionInvocation().Build()
            : Client;
    }

    public string ResolveModel(ModelPurpose purpose, string? overrideKey = null, string? explicitModel = null)
        => explicitModel ?? (purpose == ModelPurpose.Coding ? Model + "-coding" : Model);
}
