using System.Runtime.CompilerServices;
using Agent.Channels.GitLab;
using Agent.Channels.Jira;
using Agent.Core;
using Agent.Core.Agent;
using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Commands;
using Agent.Core.Events;
using Agent.Core.Pipeline;
using Agent.Core.Tasks;
using Agent.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agent.Cli.Backends;

/// <summary>
/// Runs the core in-process: LLM, tools (Jira/GitLab read tools, MCP), commands. No channels, no worker,
/// no database. The operator is treated as Admin because they hold the configuration anyway.
/// </summary>
public sealed class LocalBackend : IAgentBackend, IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly CallerIdentity _caller;
    private readonly Dictionary<string, List<ChatMessage>> _histories = new(StringComparer.Ordinal);
    private bool _started;

    public LocalBackend(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        var services = new ServiceCollection();
        services.AddSingleton(loggerFactory);
        services.AddLogging();
        services.AddAgentCore(configuration);
        services.AddAgentMcp(configuration);

        if (!string.IsNullOrWhiteSpace(configuration["Jira:BaseUrl"]))
        {
            services.AddJiraClient(configuration);
        }

        if (!string.IsNullOrWhiteSpace(configuration["GitLab:BaseUrl"]))
        {
            services.AddGitLabClient(configuration);
        }

        _services = services.BuildServiceProvider();
        _caller = CallerIdentity.Local(Environment.UserName, Role.Admin);
    }

    public string Description => "local (in-process)";

    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        foreach (var hosted in _services.GetServices<IHostedService>())
        {
            await hosted.StartAsync(ct);
        }
    }

    public async Task<ChatResult> ChatAsync(string text, string? conversationId, string? model, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
        conversationId ??= Guid.NewGuid().ToString("N");
        var evt = Event(text, conversationId);
        using var scope = RequestContext.Begin(_caller, evt);

        var dispatcher = _services.GetRequiredService<ICommandDispatcher>();
        if (dispatcher.IsCommand(text))
        {
            var result = await dispatcher.DispatchAsync(text, new CommandContext
            {
                Caller = _caller,
                Channel = Channel.Cli,
                ConversationId = conversationId,
                Event = evt,
                Services = _services,
                CancellationToken = cancellationToken,
            });
            return new ChatResult(result.Markdown, conversationId, null, [], true, result.IsError);
        }

        var response = await _services.GetRequiredService<IAgent>().AnswerAsync(Request(text, conversationId, model), cancellationToken);
        Remember(conversationId, text, response.Markdown);
        return new ChatResult(response.Markdown, conversationId, response.Model, response.ToolsUsed, false, false);
    }

    public async IAsyncEnumerable<string> StreamAsync(string text, string conversationId, string? model, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken);
        var evt = Event(text, conversationId);
        using var scope = RequestContext.Begin(_caller, evt);

        if (_services.GetRequiredService<ICommandDispatcher>().IsCommand(text))
        {
            var result = await ChatAsync(text, conversationId, model, cancellationToken);
            yield return result.Markdown;
            yield break;
        }

        var full = new System.Text.StringBuilder();
        await foreach (var chunk in _services.GetRequiredService<IAgent>().StreamAsync(Request(text, conversationId, model), cancellationToken))
        {
            full.Append(chunk);
            yield return chunk;
        }

        Remember(conversationId, text, full.ToString());
    }

    public Task<TaskInfo> CreateTaskAsync(string repoUrl, string instruction, string? title, string? baseBranch, CancellationToken cancellationToken)
        => throw new CliException("Coding tasks need the host (worker + GitLab). Use `--server` instead of `--local`.");

    public Task<IReadOnlyList<TaskInfo>> ListTasksAsync(bool all, int limit, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<TaskInfo>>([]);

    public Task<TaskInfo?> GetTaskAsync(int id, CancellationToken cancellationToken) => Task.FromResult<TaskInfo?>(null);

    public Task<IReadOnlyList<TaskEventInfo>> GetTaskEventsAsync(int id, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TaskEventInfo>>([]);

    public Task<TaskInfo?> CancelTaskAsync(int id, CancellationToken cancellationToken) => Task.FromResult<TaskInfo?>(null);

    public Task<WhoAmI> WhoAmIAsync(CancellationToken cancellationToken)
        => Task.FromResult(new WhoAmI(_caller.ChannelUserId, _caller.Username, _caller.Email, _caller.Roles.Select(r => r.ToString()).ToList(), _caller.Groups.ToList()));

    private AgentRequest Request(string text, string conversationId, string? model) => new()
    {
        Caller = _caller,
        Channel = Channel.Cli,
        ConversationId = conversationId,
        Text = text,
        History = _histories.TryGetValue(conversationId, out var h) ? h.ToArray() : [],
        ModelOverride = model,
    };

    private InboundEvent Event(string text, string conversationId) => new()
    {
        Channel = Channel.Cli,
        EventId = Guid.NewGuid().ToString("N"),
        Caller = _caller,
        ConversationId = conversationId,
        Text = text,
        IsPrivate = true,
    };

    private void Remember(string conversationId, string user, string assistant)
    {
        if (!_histories.TryGetValue(conversationId, out var list))
        {
            _histories[conversationId] = list = [];
        }

        list.Add(new ChatMessage(ChatRole.User, user));
        list.Add(new ChatMessage(ChatRole.Assistant, assistant));
        if (list.Count > 40)
        {
            list.RemoveRange(0, list.Count - 40);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_started)
        {
            foreach (var hosted in _services.GetServices<IHostedService>())
            {
                try
                {
                    await hosted.StopAsync(CancellationToken.None);
                }
                catch (Exception)
                {
                }
            }
        }

        await _services.DisposeAsync();
    }
}
