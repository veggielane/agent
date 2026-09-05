using System.Diagnostics;
using Agent.Core.Agent;
using Agent.Core.Authorization;
using Agent.Core.Commands;
using Agent.Core.Conversations;
using Agent.Core.Events;
using Agent.Core.Observability;
using Agent.Core.Replies;
using Agent.Core.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Core.Pipeline;

public interface IInboundProcessor
{
    Task ProcessAsync(InboundEvent evt, CancellationToken cancellationToken);
}

public sealed class PipelineOptions
{
    public const string SectionName = "Pipeline";

    public int HistoryMessages { get; set; } = 40;

    public int MaxConcurrency { get; set; } = 4;
}

/// <summary>identity → roles → (command | task | follow-up | question) → reply. One place, every channel.</summary>
public sealed class InboundProcessor : IInboundProcessor
{
    private readonly IProcessedEventStore _processed;
    private readonly IRoleResolver _roles;
    private readonly IAuthorizationService _authorization;
    private readonly ICommandDispatcher _commands;
    private readonly ITaskService _tasks;
    private readonly ITaskStore _taskStore;
    private readonly IAgent _agent;
    private readonly IConversationContextRouter _contexts;
    private readonly IReplyRouter _replies;
    private readonly IRepositoryResolver? _repos;
    private readonly IServiceProvider _services;
    private readonly IOptionsMonitor<AuthorizationOptions> _authOptions;
    private readonly IOptionsMonitor<PipelineOptions> _options;
    private readonly ILogger<InboundProcessor> _logger;

    public InboundProcessor(
        IProcessedEventStore processed,
        IRoleResolver roles,
        IAuthorizationService authorization,
        ICommandDispatcher commands,
        ITaskService tasks,
        ITaskStore taskStore,
        IAgent agent,
        IConversationContextRouter contexts,
        IReplyRouter replies,
        IServiceProvider services,
        IOptionsMonitor<AuthorizationOptions> authOptions,
        IOptionsMonitor<PipelineOptions> options,
        ILogger<InboundProcessor> logger,
        IRepositoryResolver? repos = null)
    {
        _processed = processed;
        _roles = roles;
        _authorization = authorization;
        _commands = commands;
        _tasks = tasks;
        _taskStore = taskStore;
        _agent = agent;
        _contexts = contexts;
        _replies = replies;
        _services = services;
        _authOptions = authOptions;
        _options = options;
        _logger = logger;
        _repos = repos;
    }

    public async Task ProcessAsync(InboundEvent evt, CancellationToken cancellationToken)
    {
        if (!await _processed.TryMarkProcessedAsync(evt.Channel, evt.EventId, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug("Skipping already processed {Channel}:{EventId}", evt.Channel, evt.EventId);
            AgentTelemetry.EventsSkipped.Add(1, new KeyValuePair<string, object?>("channel", evt.Channel.ToString()));
            return;
        }

        using var activity = AgentTelemetry.Source.StartActivity("agent.event", ActivityKind.Consumer);
        activity
            .Tag("agent.channel", evt.Channel.ToString())
            .Tag("agent.event.kind", evt.Kind.ToString())
            .Tag("agent.event.id", evt.EventId)
            .Tag("agent.conversation.id", evt.ConversationId)
            .Tag("agent.private", evt.IsPrivate);

        var started = Stopwatch.GetTimestamp();
        var outcome = "unknown";

        var caller = await _roles.ResolveAsync(evt.Caller, cancellationToken).ConfigureAwait(false);
        evt = evt with { Caller = caller };
        activity
            .Tag("agent.caller", caller.DisplayName)
            .Tag("agent.caller.roles", string.Join(",", caller.Roles.Order()));

        using var scope = RequestContext.Begin(caller, evt);

        try
        {
            if (_commands.IsCommand(evt.Text))
            {
                outcome = "command";
                await HandleCommandAsync(evt, caller, cancellationToken).ConfigureAwait(false);
                return;
            }

            switch (evt.Kind)
            {
                case InboundKind.TaskRequest:
                    outcome = "task";
                    await HandleTaskRequestAsync(evt, caller, cancellationToken).ConfigureAwait(false);
                    break;
                case InboundKind.FollowUp:
                    outcome = "follow-up";
                    await HandleFollowUpAsync(evt, caller, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    outcome = "answer";
                    await HandleQuestionAsync(evt, caller, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "cancelled";
            throw;
        }
        catch (Exception ex)
        {
            outcome = "error";
            activity.Failed(ex);
            var reference = Guid.NewGuid().ToString("N")[..6];
            activity.Tag("agent.error.reference", reference);
            _logger.LogError(ex, "Processing {Channel}:{EventId} failed (ref {Ref})", evt.Channel, evt.EventId, reference);
            await _replies.AcknowledgeAsync(evt, AckState.Failed, ex.Message, CancellationToken.None).ConfigureAwait(false);
            await SafeReplyAsync(evt, $"Sorry, something went wrong (ref {reference}).", CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            var tags = new TagList
            {
                { "channel", evt.Channel.ToString() },
                { "kind", evt.Kind.ToString() },
                { "outcome", outcome },
            };
            AgentTelemetry.Events.Add(1, tags);
            AgentTelemetry.EventDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
            activity.Tag("agent.outcome", outcome);
        }
    }

    private async Task HandleCommandAsync(InboundEvent evt, CallerIdentity caller, CancellationToken ct)
    {
        var context = new CommandContext
        {
            Caller = caller,
            Channel = evt.Channel,
            ConversationId = evt.ConversationId,
            Event = evt,
            Services = _services,
            CancellationToken = ct,
        };

        var result = await _commands.DispatchAsync(evt.Text, context).ConfigureAwait(false);
        if (!result.Silent && !string.IsNullOrWhiteSpace(result.Markdown))
        {
            await _replies.SendAsync(evt, result.Markdown, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleTaskRequestAsync(InboundEvent evt, CallerIdentity caller, CancellationToken ct)
    {
        var auth = await _authorization.AuthorizeAsync(caller, Role.Team, "task.create", ct).ConfigureAwait(false);
        if (!auth.Allowed)
        {
            await DenyAsync(evt, ct).ConfigureAwait(false);
            return;
        }

        if (evt.Task is null)
        {
            await _replies.SendAsync(evt, "I could not work out which ticket this task belongs to.", ct).ConfigureAwait(false);
            return;
        }

        var existing = await _taskStore.FindActiveBySourceAsync(evt.Task.Source, evt.Task.SourceRef, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            await _replies.SendAsync(evt, $"Task #{existing.Id} is already {existing.Status} for {evt.Task.SourceRef}. Mention me with further instructions to add a follow-up, or use `!cancel {existing.Id}`.", ct).ConfigureAwait(false);
            return;
        }

        var request = TaskRequest.From(evt, caller);
        if (string.IsNullOrWhiteSpace(request.RepoUrl) && string.IsNullOrWhiteSpace(request.ProjectId) && _repos is not null)
        {
            var target = await _repos.ResolveAsync(evt, ct).ConfigureAwait(false);
            if (target is not null)
            {
                request = request with { RepoUrl = target.Url, ProjectId = target.ProjectId, BaseBranch = request.BaseBranch ?? target.DefaultBranch };
            }
        }

        await _replies.AcknowledgeAsync(evt, AckState.Working, null, ct).ConfigureAwait(false);
        var task = await _tasks.CreateAsync(request, ct).ConfigureAwait(false);

        var message = task.Status == AgentTaskStatus.NeedsInput
            ? $"Created task #{task.Id}, but I could not resolve a repository for it. Reply with the GitLab repository URL and mention me."
            : $"Picked up as task #{task.Id}. I'll open a merge request and report back here.";
        await _replies.SendAsync(evt, message, ct).ConfigureAwait(false);
    }

    private async Task HandleFollowUpAsync(InboundEvent evt, CallerIdentity caller, CancellationToken ct)
    {
        var auth = await _authorization.AuthorizeAsync(caller, Role.Team, "task.followup", ct).ConfigureAwait(false);
        if (!auth.Allowed)
        {
            await DenyAsync(evt, ct).ConfigureAwait(false);
            return;
        }

        AgentTask? task = null;
        if (evt.Task?.ExistingTaskId is int id)
        {
            task = await _taskStore.GetAsync(id, ct).ConfigureAwait(false);
        }

        if (task is null && evt.Task is { ProjectId: not null, MergeRequestIid: not null })
        {
            task = await _taskStore.FindByMergeRequestAsync(evt.Task.ProjectId, evt.Task.MergeRequestIid, ct).ConfigureAwait(false);
        }

        if (task is null && evt.Task is not null)
        {
            task = await _taskStore.FindActiveBySourceAsync(evt.Task.Source, evt.Task.SourceRef, ct).ConfigureAwait(false);
        }

        if (task is null && evt.Task is { ExistingBranch: not null })
        {
            // A mention on a merge request nobody is tracking yet: start a task on that branch.
            await HandleTaskRequestAsync(evt, caller, ct).ConfigureAwait(false);
            return;
        }

        if (task is null)
        {
            // Nothing to follow up on: answer it as a question instead.
            await HandleQuestionAsync(evt, caller, ct).ConfigureAwait(false);
            return;
        }

        if (task.Status == AgentTaskStatus.NeedsInput && evt.Task?.RepoUrl is null && TryExtractRepoUrl(evt.Text) is { } repo)
        {
            task.RepoUrl = repo;
            await _taskStore.UpdateAsync(task, ct).ConfigureAwait(false);
        }

        await _replies.AcknowledgeAsync(evt, AckState.Working, null, ct).ConfigureAwait(false);
        var updated = await _tasks.AddFollowUpAsync(task.Id, evt.Text, caller, ct).ConfigureAwait(false);
        await _replies.SendAsync(evt, updated.Status == AgentTaskStatus.Queued
            ? $"Got it — task #{updated.Id} is queued to work on that."
            : $"Noted for task #{updated.Id} (currently {updated.Status}); I'll pick it up when the current run finishes.", ct).ConfigureAwait(false);
    }

    private async Task HandleQuestionAsync(InboundEvent evt, CallerIdentity caller, CancellationToken ct)
    {
        var auth = await _authorization.AuthorizeAsync(caller, Role.Users, "ask", ct).ConfigureAwait(false);
        if (!auth.Allowed)
        {
            await DenyAsync(evt, ct).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(evt.Text))
        {
            await _replies.SendAsync(evt, "Hi! Ask me a question or use `!help` to see commands.", ct).ConfigureAwait(false);
            return;
        }

        await _replies.AcknowledgeAsync(evt, AckState.Working, null, ct).ConfigureAwait(false);
        var history = await _contexts.GetHistoryAsync(evt, _options.CurrentValue.HistoryMessages, ct).ConfigureAwait(false);

        var response = await _agent.AnswerAsync(new AgentRequest
        {
            Caller = caller,
            Channel = evt.Channel,
            ConversationId = evt.ConversationId,
            Text = evt.Text,
            History = history,
        }, ct).ConfigureAwait(false);

        await _replies.SendAsync(evt, response.Markdown, ct).ConfigureAwait(false);
        await _replies.AcknowledgeAsync(evt, AckState.Done, null, ct).ConfigureAwait(false);
    }

    private async Task DenyAsync(InboundEvent evt, CancellationToken ct)
    {
        var options = _authOptions.CurrentValue;
        if (options.DenyBehaviour == DenyBehaviour.Reply || evt.IsPrivate)
        {
            await _replies.SendAsync(evt, options.DenyMessage, ct).ConfigureAwait(false);
        }
    }

    private async Task SafeReplyAsync(InboundEvent evt, string text, CancellationToken ct)
    {
        try
        {
            await _replies.SendAsync(evt, text, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not send error reply for {Channel}:{EventId}", evt.Channel, evt.EventId);
        }
    }

    internal static string? TryExtractRepoUrl(string text)
    {
        foreach (var token in text.Split([' ', '\n', '\r', '\t', '<', '>', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (Uri.TryCreate(token.TrimEnd('.', ','), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.AbsolutePath.Trim('/').Contains('/'))
            {
                return uri.ToString();
            }
        }

        return null;
    }
}
