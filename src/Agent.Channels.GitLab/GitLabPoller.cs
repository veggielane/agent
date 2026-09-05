using System.Collections.Concurrent;
using System.Globalization;
using Agent.Core.Channels;
using Agent.Core.Events;
using Agent.Core.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Channels.GitLab;

/// <summary>
/// Polls GitLab every <see cref="GitLabOptions.PollSeconds"/>: the bot's to-do list (mentions and assignments),
/// labelled issues per configured group, and the state of merge requests behind tasks awaiting review.
/// </summary>
public sealed class GitLabPoller : BackgroundService, IEventSource
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProjectCacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan UserCacheTtl = TimeSpan.FromHours(1);

    private readonly IGitLabClient _client;
    private readonly IInboundQueue _queue;
    private readonly IProcessedEventStore _processed;
    private readonly ICursorStore _cursors;
    private readonly ITaskStore _tasks;
    private readonly ITaskService _taskService;
    private readonly IOptionsMonitor<GitLabOptions> _options;
    private readonly ILogger<GitLabPoller> _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, CacheEntry<GitLabProject?>> _projects = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, CacheEntry<GitLabUser?>> _users = new();

    private DateTimeOffset? _lastPoll;
    private string? _lastError;
    private int _todosProcessed;
    private int _issuesProcessed;
    private int _mergeRequestsClosed;

    public GitLabPoller(
        IGitLabClient client,
        IInboundQueue queue,
        IProcessedEventStore processed,
        ICursorStore cursors,
        ITaskStore tasks,
        ITaskService taskService,
        IOptionsMonitor<GitLabOptions> options,
        ILogger<GitLabPoller> logger,
        TimeProvider? timeProvider = null)
    {
        _client = client;
        _queue = queue;
        _processed = processed;
        _cursors = cursors;
        _tasks = tasks;
        _taskService = taskService;
        _options = options;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string Name => "GitLab poller";

    public string Status
    {
        get
        {
            var last = _lastPoll is null ? "never" : _lastPoll.Value.ToString("u", CultureInfo.InvariantCulture);
            var error = _lastError is null ? "none" : _lastError;
            return $"last poll {last}; to-dos {_todosProcessed}, labelled issues {_issuesProcessed}, MRs closed {_mergeRequestsClosed}; last error: {error}";
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var ok = await PollOnceAsync(stoppingToken).ConfigureAwait(false);

            // A healthy poll that found nothing still counts, so silence on this metric means broken.
            Agent.Core.Observability.AgentTelemetry.Polls.Add(1, new System.Diagnostics.TagList
            {
                { "channel", "GitLab" },
                { "outcome", ok ? "ok" : "error" },
            });

            failures = ok ? 0 : failures + 1;
            var interval = TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.PollSeconds));
            var delay = ok ? interval : Backoff(interval, failures);
            try
            {
                await Task.Delay(delay, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Exponential backoff after consecutive failed polls, capped at five minutes.</summary>
    internal static TimeSpan Backoff(TimeSpan interval, int failures)
    {
        var factor = Math.Pow(2, Math.Clamp(failures, 0, 16));
        var delay = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, interval.TotalSeconds * factor));
        return delay < interval ? interval : delay;
    }

    /// <summary>One full poll. Returns false when any step failed (errors are logged and kept in <see cref="Status"/>).</summary>
    internal async Task<bool> PollOnceAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var mapper = new GitLabEventMapper(options);
        var ok = true;

        ok &= await RunStepAsync("to-dos", ct => PollTodosAsync(options, mapper, ct), cancellationToken).ConfigureAwait(false);
        ok &= await RunStepAsync("labelled issues", ct => PollLabelledIssuesAsync(options, mapper, ct), cancellationToken).ConfigureAwait(false);
        ok &= await RunStepAsync("merge request state", PollMergeRequestStatesAsync, cancellationToken).ConfigureAwait(false);

        _lastPoll = _time.GetUtcNow();
        if (ok)
        {
            _lastError = null;
        }

        return ok;
    }

    private async Task<bool> RunStepAsync(string step, Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GitLab poll step '{Step}' failed", step);
            _lastError = $"{step}: {ex.Message}";
            return false;
        }
    }

    // ---- (a) to-dos -------------------------------------------------------------------------------------------

    private async Task PollTodosAsync(GitLabOptions options, GitLabEventMapper mapper, CancellationToken cancellationToken)
    {
        var todos = await _client.GetTodosAsync(cancellationToken).ConfigureAwait(false);
        var failures = new List<string>();
        foreach (var todo in todos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ProcessTodoAsync(todo, mapper, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Processing GitLab to-do {TodoId} ({Action} on {TargetType}) failed", todo.Id, todo.ActionName, todo.TargetType);
                failures.Add($"to-do {todo.Id.ToString(CultureInfo.InvariantCulture)}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException($"{failures.Count.ToString(CultureInfo.InvariantCulture)} of {todos.Count.ToString(CultureInfo.InvariantCulture)} failed ({string.Join("; ", failures)})");
        }
    }

    private async Task ProcessTodoAsync(GitLabTodo todo, GitLabEventMapper mapper, CancellationToken cancellationToken)
    {
        var disposition = mapper.Classify(todo);
        if (disposition == TodoDisposition.Ignore)
        {
            _logger.LogDebug("Ignoring GitLab to-do {TodoId} ({Action} on {TargetType})", todo.Id, todo.ActionName, todo.TargetType);
            await _client.MarkTodoDoneAsync(todo.Id, cancellationToken).ConfigureAwait(false);
            return;
        }

        var evt = disposition == TodoDisposition.Mention
            ? await BuildMentionAsync(todo, mapper, cancellationToken).ConfigureAwait(false)
            : await BuildAssignmentAsync(todo, mapper, cancellationToken).ConfigureAwait(false);

        if (evt is null)
        {
            await _client.MarkTodoDoneAsync(todo.Id, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Mark before enqueue: a crash between the two loses at most one event, never duplicates one.
        if (!await _processed.TryMarkProcessedAsync(Channel.GitLab, $"gitlab-todo:{todo.Id.ToString(CultureInfo.InvariantCulture)}", cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug("GitLab to-do {TodoId} already processed; marking done again", todo.Id);
            await _client.MarkTodoDoneAsync(todo.Id, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _queue.EnqueueAsync(evt, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _todosProcessed);
        _logger.LogInformation("GitLab to-do {TodoId} → {Kind} on {Conversation} by {User}", todo.Id, evt.Kind, evt.ConversationId, evt.Caller.DisplayName);
        await _client.MarkTodoDoneAsync(todo.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<InboundEvent?> BuildMentionAsync(GitLabTodo todo, GitLabEventMapper mapper, CancellationToken cancellationToken)
    {
        var project = todo.Project!;
        var target = todo.Target!;
        var projectId = project.Id.ToString(CultureInfo.InvariantCulture);
        var path = project.PathWithNamespace;
        GitLabNoteableTypeExtensions.TryParse(todo.TargetType, out var type);

        var authorDetails = await GetUserCachedAsync(todo.Author!.Id, cancellationToken).ConfigureAwait(false);
        var projectDetails = await GetProjectCachedAsync(projectId, cancellationToken).ConfigureAwait(false);

        AgentTask? task;
        GitLabMergeRequest? mergeRequest = null;
        string? discussionId = null;
        GitLabNotePosition? position = null;

        if (type == GitLabNoteableType.Issue)
        {
            task = await _tasks.FindActiveBySourceAsync(TaskSource.GitLabIssue, GitLabRefs.Issue(path, target.Iid), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var iid = target.Iid.ToString(CultureInfo.InvariantCulture);
            task = await _tasks.FindByMergeRequestAsync(projectId, iid, cancellationToken).ConfigureAwait(false)
                   ?? await _tasks.FindByMergeRequestAsync(path, iid, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(target.SourceBranch) || string.IsNullOrEmpty(target.TargetBranch))
            {
                mergeRequest = await _client.GetMergeRequestAsync(projectId, target.Iid, cancellationToken).ConfigureAwait(false);
            }

            if (GitLabEventMapper.ParseNoteId(todo.TargetUrl) is { } noteId)
            {
                (discussionId, position) = await FindDiscussionAsync(projectId, target.Iid, noteId, cancellationToken).ConfigureAwait(false);
            }
        }

        return mapper.MapMention(new MentionInput(todo, authorDetails, projectDetails, task, mergeRequest, discussionId, position));
    }

    private async Task<InboundEvent?> BuildAssignmentAsync(GitLabTodo todo, GitLabEventMapper mapper, CancellationToken cancellationToken)
    {
        var projectRef = todo.Project!;
        var target = todo.Target!;
        var projectId = projectRef.Id.ToString(CultureInfo.InvariantCulture);
        var sourceRef = GitLabRefs.Issue(projectRef.PathWithNamespace, target.Iid);

        var existing = await _tasks.FindActiveBySourceAsync(TaskSource.GitLabIssue, sourceRef, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            _logger.LogInformation("Issue {Ref} assigned to the bot already has active task {Task}; ignoring to-do {TodoId}", sourceRef, existing.DisplayRef, todo.Id);
            return null;
        }

        var project = await GetProjectCachedAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project {projectId} ({projectRef.PathWithNamespace}) was not found.");
        var authorDetails = await GetUserCachedAsync(todo.Author!.Id, cancellationToken).ConfigureAwait(false);
        return mapper.MapAssignment(new AssignmentInput(todo, project, authorDetails));
    }

    /// <summary>Best effort: the discussion holding the note, for threaded replies and the diff position.</summary>
    private async Task<(string? DiscussionId, GitLabNotePosition? Position)> FindDiscussionAsync(string projectId, long mergeRequestIid, long noteId, CancellationToken cancellationToken)
    {
        try
        {
            var discussions = await _client.GetMergeRequestDiscussionsAsync(projectId, mergeRequestIid, cancellationToken).ConfigureAwait(false);
            foreach (var discussion in discussions)
            {
                var note = discussion.Notes.FirstOrDefault(n => n.Id == noteId);
                if (note is null)
                {
                    continue;
                }

                var position = note.Position ?? discussion.Notes.FirstOrDefault(n => n.Position is not null)?.Position;
                return (discussion.IndividualNote ? null : discussion.Id, position);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load discussions for MR {Project}!{Iid}", projectId, mergeRequestIid);
        }

        return (null, null);
    }

    // ---- (b) labelled issues ----------------------------------------------------------------------------------

    private async Task PollLabelledIssuesAsync(GitLabOptions options, GitLabEventMapper mapper, CancellationToken cancellationToken)
    {
        if (options.Groups.Length == 0 || string.IsNullOrWhiteSpace(options.TaskLabel))
        {
            return;
        }

        var overlap = TimeSpan.FromMinutes(Math.Max(0, options.OverlapMinutes));
        var failures = new List<string>();
        foreach (var group in options.Groups.Where(g => !string.IsNullOrWhiteSpace(g)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await PollGroupAsync(group.Trim(), options, mapper, overlap, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Polling labelled issues of GitLab group {Group} failed", group);
                failures.Add($"{group}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(string.Join("; ", failures));
        }
    }

    private async Task PollGroupAsync(string group, GitLabOptions options, GitLabEventMapper mapper, TimeSpan overlap, CancellationToken cancellationToken)
    {
        var key = $"gitlab:issues:{group}";
        var now = _time.GetUtcNow();
        var stored = await _cursors.GetAsync(key, cancellationToken).ConfigureAwait(false);
        var watermark = stored is not null && DateTimeOffset.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : now - overlap;

        var issues = await _client.GetGroupIssuesAsync(group, options.TaskLabel, watermark - overlap, "opened", cancellationToken).ConfigureAwait(false);
        var max = watermark;
        foreach (var issue in issues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (issue.UpdatedAt > max)
            {
                max = issue.UpdatedAt;
            }

            await ProcessLabelledIssueAsync(issue, mapper, cancellationToken).ConfigureAwait(false);
        }

        if (max > watermark || stored is null)
        {
            await _cursors.SetAsync(key, max.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessLabelledIssueAsync(GitLabIssue issue, GitLabEventMapper mapper, CancellationToken cancellationToken)
    {
        if (issue.Author is null)
        {
            _logger.LogWarning("Labelled issue {Id} (iid {Iid}) has no author; skipping", issue.Id, issue.Iid);
            return;
        }

        var projectId = issue.ProjectId.ToString(CultureInfo.InvariantCulture);
        var project = await GetProjectCachedAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            _logger.LogWarning("Project {ProjectId} of labelled issue {Iid} was not found; skipping", projectId, issue.Iid);
            return;
        }

        var sourceRef = GitLabRefs.Issue(project.PathWithNamespace, issue.Iid);
        if (await _tasks.FindActiveBySourceAsync(TaskSource.GitLabIssue, sourceRef, cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        var key = $"gitlab-label:{projectId}:{issue.Iid.ToString(CultureInfo.InvariantCulture)}";
        if (!await _processed.TryMarkProcessedAsync(Channel.GitLab, key, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var authorDetails = await GetUserCachedAsync(issue.Author.Id, cancellationToken).ConfigureAwait(false);
        var evt = mapper.MapLabelledIssue(issue, project, authorDetails);
        await _queue.EnqueueAsync(evt, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _issuesProcessed);
        _logger.LogInformation("Labelled issue {Ref} → task request by {User}", sourceRef, evt.Caller.DisplayName);
    }

    // ---- (c) merge request state ------------------------------------------------------------------------------

    private async Task PollMergeRequestStatesAsync(CancellationToken cancellationToken)
    {
        var awaiting = await _tasks.ListAsync(new TaskQuery { Statuses = [AgentTaskStatus.AwaitingReview], Limit = 200 }, cancellationToken).ConfigureAwait(false);
        foreach (var task in awaiting)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(task.ProjectId) || !long.TryParse(task.MergeRequestIid, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iid))
            {
                continue;
            }

            var mr = await _client.GetMergeRequestAsync(task.ProjectId, iid, cancellationToken).ConfigureAwait(false);
            if (mr is null)
            {
                continue;
            }

            var merged = string.Equals(mr.State, "merged", StringComparison.OrdinalIgnoreCase);
            var closed = string.Equals(mr.State, "closed", StringComparison.OrdinalIgnoreCase);
            if (!merged && !closed)
            {
                continue;
            }

            await _taskService.CloseAsync(task.Id, merged, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _mergeRequestsClosed);
            _logger.LogInformation("Task {Task}: merge request !{Iid} is {State}", task.DisplayRef, iid, mr.State);
        }
    }

    // ---- caches -----------------------------------------------------------------------------------------------

    private async Task<GitLabProject?> GetProjectCachedAsync(string projectId, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (_projects.TryGetValue(projectId, out var cached) && cached.Expires > now)
        {
            return cached.Value;
        }

        var project = await _client.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        _projects[projectId] = new CacheEntry<GitLabProject?>(project, now + ProjectCacheTtl);
        return project;
    }

    /// <summary>Best effort: the full user record adds email and LDAP identity; a failure leaves them unset.</summary>
    private async Task<GitLabUser?> GetUserCachedAsync(long userId, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (_users.TryGetValue(userId, out var cached) && cached.Expires > now)
        {
            return cached.Value;
        }

        GitLabUser? user = null;
        try
        {
            user = await _client.GetUserAsync(userId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load GitLab user {UserId}", userId);
        }

        _users[userId] = new CacheEntry<GitLabUser?>(user, now + (user is null ? TimeSpan.FromMinutes(5) : UserCacheTtl));
        return user;
    }

    private readonly record struct CacheEntry<T>(T Value, DateTimeOffset Expires);
}
