using Agent.Core.Audit;
using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Commands;
using Agent.Core.Conversations;
using Agent.Core.Events;
using Agent.Core.Llm;
using Agent.Core.Replies;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Core.Tests.Support;

/// <summary>A fully wired core with fakes at the edges: scripted LLM, capturing reply sender, static groups.</summary>
public sealed class TestHost : IDisposable
{
    private TestHost(ServiceProvider services, FakeChatClientFactory llm, InMemoryAuditSink audit, CapturingReplySender replies, FakeContextProvider context, string workDir)
    {
        Services = services;
        Llm = llm;
        Audit = audit;
        Replies = replies;
        Context = context;
        WorkDir = workDir;
    }

    public ServiceProvider Services { get; }

    public FakeChatClientFactory Llm { get; }

    public InMemoryAuditSink Audit { get; }

    public CapturingReplySender Replies { get; }

    public FakeContextProvider Context { get; }

    /// <summary>Temp directory used as the commands directory (and prompts directory below it).</summary>
    public string WorkDir { get; }

    public string CommandsDir => WorkDir;

    public static TestHost Create(Action<IServiceCollection>? configure = null, IDictionary<string, string?>? extraConfig = null, Channel channel = Channel.Mattermost)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "agent-core-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        var config = new Dictionary<string, string?>
        {
            ["Llm:BaseUrl"] = "http://localhost:1/v1",
            ["Llm:AnswerModel"] = "test-model",
            ["Llm:CodingModel"] = "test-coding",
            ["Authorization:Provider"] = "Static",
            ["Authorization:Roles:Users:0"] = "g-users",
            ["Authorization:Roles:Team:0"] = "g-team",
            ["Authorization:Roles:Admin:0"] = "g-admin",
            ["Authorization:StaticGroups:alice:0"] = "g-users",
            ["Authorization:StaticGroups:bob:0"] = "g-team",
            ["Authorization:StaticGroups:root:0"] = "g-admin",
            ["Authorization:CacheMinutes"] = "0",
            ["Commands:Directory"] = workDir,
            ["Prompts:Directory"] = Path.Combine(workDir, "prompts"),
        };

        if (extraConfig is not null)
        {
            foreach (var (k, v) in extraConfig)
            {
                config[k] = v;
            }
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();

        var llm = new FakeChatClientFactory();
        var audit = new InMemoryAuditSink();
        var replies = new CapturingReplySender(channel);
        var context = new FakeContextProvider(channel);

        services.AddSingleton<IChatClientFactory>(llm);
        services.AddSingleton<IAuditSink>(audit);
        services.AddSingleton<IReplySender>(replies);
        services.AddSingleton<IConversationContextProvider>(context);
        configure?.Invoke(services);
        services.AddAgentCore(configuration);

        return new TestHost(services.BuildServiceProvider(), llm, audit, replies, context, workDir);
    }

    public T Get<T>()
        where T : notnull
        => Services.GetRequiredService<T>();

    public static CallerIdentity Caller(string name, Channel channel = Channel.Mattermost)
        => new(channel, name, name, $"{name}@corp.local");

    public InboundEvent Event(
        string text,
        string caller = "alice",
        InboundKind kind = InboundKind.Message,
        bool isPrivate = false,
        TaskContext? task = null,
        string? conversationId = null,
        string? eventId = null,
        Channel? channel = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var ch = channel ?? Replies.Channel;
        return new InboundEvent
        {
            Channel = ch,
            EventId = eventId ?? Guid.NewGuid().ToString("N"),
            Caller = Caller(caller, ch),
            ConversationId = conversationId ?? "conv-1",
            Text = text,
            Kind = kind,
            IsPrivate = isPrivate,
            Task = task,
            Metadata = metadata ?? new Dictionary<string, string>(),
        };
    }

    public CommandContext CommandContext(CallerIdentity caller, string conversationId = "conv-1", InboundEvent? evt = null)
        => new()
        {
            Caller = caller,
            Channel = caller.Channel,
            ConversationId = conversationId,
            Event = evt,
            Services = Services,
            CancellationToken = TestContext.Current.CancellationToken,
        };

    public async Task<CallerIdentity> ResolveAsync(string name, Channel channel = Channel.Mattermost)
        => await Get<IRoleResolver>().ResolveAsync(Caller(name, channel), TestContext.Current.CancellationToken);

    public void Dispose()
    {
        Services.Dispose();
        try
        {
            Directory.Delete(WorkDir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class CapturingReplySender : IReplySender
{
    public CapturingReplySender(Channel channel) => Channel = channel;

    public Channel Channel { get; }

    public List<(InboundEvent Event, string Text)> Sent { get; } = [];

    public List<(InboundEvent Event, AckState State, string? Note)> Acks { get; } = [];

    public string? Last => Sent.Count == 0 ? null : Sent[^1].Text;

    public Task SendAsync(InboundEvent source, string text, CancellationToken cancellationToken)
    {
        Sent.Add((source, text));
        return Task.CompletedTask;
    }

    public Task AcknowledgeAsync(InboundEvent source, AckState state, string? note, CancellationToken cancellationToken)
    {
        Acks.Add((source, state, note));
        return Task.CompletedTask;
    }
}

public sealed class FakeContextProvider : IConversationContextProvider
{
    public FakeContextProvider(Channel channel) => Channel = channel;

    public Channel Channel { get; }

    public List<ChatMessage> History { get; } = [];

    public Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(InboundEvent evt, int maxMessages, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ChatMessage>>(History.TakeLast(maxMessages).ToList());
}
