using Microsoft.Extensions.AI;

namespace Agent.Coding.Tests;

/// <summary>
/// An <see cref="IChatClient"/> that plays back pre-planned responses. Wrapped with
/// <c>UseFunctionInvocation()</c> it drives the coding engine's tools exactly like a real model would.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse>> _steps = new();
    private int _callIds;

    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    public ChatOptions? LastOptions { get; private set; }

    /// <summary>Used once the scripted steps are exhausted. Throws by default.</summary>
    public Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse>? Fallback { get; set; }

    public ScriptedChatClient Then(Func<IReadOnlyList<ChatMessage>, ChatOptions?, ChatResponse> step)
    {
        _steps.Enqueue(step);
        return this;
    }

    public ScriptedChatClient ThenToolCall(string name, IDictionary<string, object?>? arguments = null)
        => Then((_, _) => ToolCall(name, arguments));

    public ScriptedChatClient ThenText(string text) => Then((_, _) => Text(text));

    public ChatResponse ToolCall(string name, IDictionary<string, object?>? arguments = null)
    {
        var id = "call_" + Interlocked.Increment(ref _callIds);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(id, name, arguments)]))
        {
            Usage = new UsageDetails { InputTokenCount = 5, OutputTokenCount = 5, TotalTokenCount = 10 },
        };
    }

    public static ChatResponse Text(string text)
        => new(new ChatMessage(ChatRole.Assistant, text))
        {
            Usage = new UsageDetails { InputTokenCount = 5, OutputTokenCount = 5, TotalTokenCount = 10 },
        };

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var list = messages.ToList();
        Calls.Add(list);
        LastOptions = options;
        var step = _steps.Count > 0
            ? _steps.Dequeue()
            : Fallback ?? throw new InvalidOperationException($"Script exhausted after {Calls.Count} calls.");
        return Task.FromResult(step(list, options));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
