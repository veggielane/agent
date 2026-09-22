using System.Globalization;
using System.Text;
using Agent.Coding;
using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Worker;

/// <summary>Drives one claimed task from Preparing to AwaitingReview (or Failed / NeedsInput / Cancelled).</summary>
public interface ITaskRunner
{
    Task RunAsync(AgentTask task, CancellationToken cancellationToken);
}

public sealed class TaskRunner : ITaskRunner
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(10);

    /// <summary>How many changed files the "pushed" event names before it counts the rest.</summary>
    private const int MaxListedFiles = 40;

    private readonly ITaskStore _store;
    private readonly ITaskCancellationRegistry _cancellations;
    private readonly IWorkspaceManager _workspaces;
    private readonly ICodingEngine _engine;
    private readonly IGitRunner _git;
    private readonly ITaskNotifierRouter _notifier;
    private readonly IOptionsMonitor<CodingOptions> _options;
    private readonly ILogger<TaskRunner> _logger;
    private readonly IRepositoryCredentialProvider? _credentials;
    private readonly IMergeRequestPublisher? _mergeRequests;
    private readonly ITaskPlanner? _planner;
    private readonly IRepositoryPolicy _repositories;

    /// <param name="repositories">
    /// Checked again before cloning, so a task whose repository was attached outside the usual paths (an edited
    /// row, an old task after the allow list changed) fails with the reason instead of running.
    /// </param>
    public TaskRunner(
        ITaskStore store,
        ITaskCancellationRegistry cancellations,
        IWorkspaceManager workspaces,
        ICodingEngine engine,
        IGitRunner git,
        ITaskNotifierRouter notifier,
        IOptionsMonitor<CodingOptions> options,
        ILogger<TaskRunner> logger,
        IRepositoryCredentialProvider? credentials = null,
        IMergeRequestPublisher? mergeRequests = null,
        ITaskPlanner? planner = null,
        IRepositoryPolicy? repositories = null)
    {
        _store = store;
        _cancellations = cancellations;
        _workspaces = workspaces;
        _engine = engine;
        _git = git;
        _notifier = notifier;
        _options = options;
        _logger = logger;
        _credentials = credentials;
        _mergeRequests = mergeRequests;
        _planner = planner;
        _repositories = repositories ?? new AllowAllRepositoryPolicy();
    }

    public async Task RunAsync(AgentTask task, CancellationToken cancellationToken)
    {
        var token = _cancellations.Register(task.Id, cancellationToken);
        Workspace? workspace = null;
        var keepWorkspace = false;

        try
        {
            workspace = await PrepareAsync(task, token).ConfigureAwait(false);
            if (workspace is null)
            {
                return;
            }

            await AnnouncePlanAsync(task, workspace, token).ConfigureAwait(false);

            var result = await WorkAsync(task, workspace, token).ConfigureAwait(false);
            if (result.StopReason == CodingStopReason.Cancelled)
            {
                throw new OperationCanceledException(token);
            }

            if (result.StopReason == CodingStopReason.Error)
            {
                throw new InvalidOperationException(result.Error ?? "The coding engine reported an error.");
            }

            await PublishAsync(task, workspace, result, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                keepWorkspace = true;
                await InterruptedAsync(task).ConfigureAwait(false);
            }
            else
            {
                await CancelledAsync(task).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            keepWorkspace = true;
            await FailedAsync(task, ex).ConfigureAwait(false);
        }
        finally
        {
            _cancellations.Unregister(task.Id);
            if (workspace is not null)
            {
                try
                {
                    await _workspaces.CleanupAsync(workspace, keepWorkspace, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Workspace cleanup failed for task {Task}", task.DisplayRef);
                }
            }
        }
    }

    private async Task<Workspace?> PrepareAsync(AgentTask task, CancellationToken token)
    {
        task.Status = AgentTaskStatus.Preparing;
        task.Error = null;
        await SaveAsync(task, token).ConfigureAwait(false);
        await EventAsync(task, "preparing", $"Attempt {task.Attempts}: preparing workspace").ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(task.RepoUrl))
        {
            task.Status = AgentTaskStatus.NeedsInput;
            task.Summary = "No repository is associated with this task.";
            await SaveAsync(task, token).ConfigureAwait(false);
            await EventAsync(task, "needs-input", "No repository URL").ConfigureAwait(false);
            await NotifyAsync(task, Terminal(task, "needs-input", "I don't know which repository this task belongs to. Tell me the repository (or set it on the ticket) and I'll start.")).ConfigureAwait(false);
            return null;
        }

        var decision = _repositories.Check(task.RepoUrl);
        if (!decision.Allowed)
        {
            var reason = decision.Reason ?? "The repository is not allowed.";
            task.Status = AgentTaskStatus.Failed;
            task.Error = TextUtil.TruncateEnd(reason, 2000);
            await SaveAsync(task, token).ConfigureAwait(false);
            await EventAsync(task, "failed", $"Repository not allowed: {task.RepoUrl}").ConfigureAwait(false);
            await NotifyAsync(task, Terminal(task, "repository-denied", "I can't work on this repository. " + reason)).ConfigureAwait(false);
            return null;
        }

        var credentials = _credentials?.GetCredentials(task.RepoUrl);
        var workspace = await _workspaces.PrepareAsync(task, credentials, token).ConfigureAwait(false);

        task.WorkBranch = workspace.Branch;
        task.BaseBranch = workspace.BaseBranch;
        task.Metadata["workspace"] = workspace.Root;
        await SaveAsync(task, token).ConfigureAwait(false);
        await EventAsync(task, "branch", $"Working on {workspace.Branch} (base {workspace.BaseBranch})").ConfigureAwait(false);
        return workspace;
    }

    /// <summary>
    /// Says what it understood and how it intends to proceed, before writing anything. The task keeps
    /// running: this is a chance to cancel with the branch still empty, not an approval gate. The merge
    /// request is the approval gate.
    /// </summary>
    private async Task AnnouncePlanAsync(AgentTask task, Workspace workspace, CancellationToken token)
    {
        if (_planner is null || !_options.CurrentValue.PostPlan || !string.IsNullOrWhiteSpace(task.Summary))
        {
            // Follow-up runs already have context in the thread; only the first run announces a plan.
            return;
        }

        var plan = await _planner.PlanAsync(workspace, task.Instruction, Requester(task), token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(plan))
        {
            return;
        }

        await EventAsync(task, "plan", TextUtil.TruncateEnd(TextUtil.FirstLine(plan), 200)).ConfigureAwait(false);

        // Keyed without the attempt: a worker that restarts mid-run must not announce the same plan twice.
        await NotifyAsync(task, new TaskNotification("plan", $"{plan}\n\n_Working on `{workspace.Branch}`. Cancel with `!cancel {task.Id}` if this is not what you meant._")).ConfigureAwait(false);
    }

    private async Task<CodingResult> WorkAsync(AgentTask task, Workspace workspace, CancellationToken token)
    {
        var pending = string.IsNullOrWhiteSpace(task.PendingInstruction) ? null : task.PendingInstruction.Trim();
        var firstRun = string.IsNullOrWhiteSpace(task.Summary);

        string instruction;
        if (firstRun)
        {
            instruction = pending is null ? task.Instruction : task.Instruction + "\n\nAdditional instructions:\n" + pending;
        }
        else
        {
            instruction = pending ?? task.Instruction;
        }

        task.PendingInstruction = null;
        task.Status = AgentTaskStatus.Working;
        await SaveAsync(task, token).ConfigureAwait(false);
        await EventAsync(task, "working", firstRun ? "Coding started" : "Follow-up started").ConfigureAwait(false);

        var actions = new ActionLog(action => EventAsync(task, ActionEventType(action.Kind), action.Detail), _logger, task);
        var run = new CodingRun(workspace, instruction, firstRun ? null : task.Summary, IsFollowUp: !firstRun, Requester(task), actions);
        var progress = new ThrottledProgress(message => EventAsync(task, "progress", message), ProgressInterval);

        CodingResult result;
        try
        {
            result = await _engine.RunAsync(run, progress, token).ConfigureAwait(false);
        }
        finally
        {
            // The trail must be complete before the outcome is recorded, whatever the outcome was.
            await actions.Completion.ConfigureAwait(false);
        }

        task.Turns += result.Turns;
        task.TokensUsed += result.TokensUsed;
        task.Summary = result.Summary;
        return result;
    }

    private async Task PublishAsync(AgentTask task, Workspace workspace, CodingResult result, CancellationToken token)
    {
        var options = _options.CurrentValue;
        var exhausted = result.StopReason != CodingStopReason.Done;

        task.Status = AgentTaskStatus.Publishing;
        await SaveAsync(task, token).ConfigureAwait(false);

        if (!await _git.HasChangesAsync(workspace.RepoPath, token).ConfigureAwait(false))
        {
            task.Status = AgentTaskStatus.NeedsInput;
            task.Summary = "I made no changes: " + result.Summary;
            await SaveAsync(task, token).ConfigureAwait(false);
            await EventAsync(task, "needs-input", "No changes were produced").ConfigureAwait(false);

            var noChanges = new StringBuilder("I made no changes.\n\n").Append(result.Summary);
            if (exhausted)
            {
                noChanges.Append("\n\nThe budget was exhausted (").Append(result.StopReason).Append(") before the work was finished.");
            }

            noChanges.Append("\n\nReply with more details to continue.");
            await NotifyAsync(task, Terminal(task, "no-changes", noChanges.ToString())).ConfigureAwait(false);
            return;
        }

        var branch = task.WorkBranch ?? workspace.Branch;
        var credentials = _credentials?.GetCredentials(task.RepoUrl!);
        await _git.AddAllAsync(workspace.RepoPath, token).ConfigureAwait(false);
        await _git.CommitAsync(workspace.RepoPath, CommitMessage(task, result, options.ConventionalCommits), options.CommitAuthorName, options.CommitAuthorEmail, token).ConfigureAwait(false);
        await _git.PushAsync(workspace.RepoPath, branch, credentials, setUpstream: true, token).ConfigureAwait(false);
        await EventAsync(task, "pushed", PushedMessage(branch, result.ChangedFiles)).ConfigureAwait(false);

        var verification = result.Verified switch
        {
            true => "yes",
            false => "no (see MR description)",
            null => "n.a. (no build/test command)",
        };

        if (_mergeRequests is not null && !string.IsNullOrWhiteSpace(task.ProjectId))
        {
            var spec = new MergeRequestSpec(
                task.ProjectId,
                branch,
                task.BaseBranch ?? workspace.BaseBranch ?? "main",
                $"{task.SourceRef}: {TextUtil.TruncateEnd(task.Title ?? TextUtil.FirstLine(result.Summary), 200)}",
                MergeRequestDescription(task, result),
                Draft: options.OpenAsDraft || result.Verified != true || exhausted,
                ReviewerUsername: task.RequesterUsername);

            var info = await _mergeRequests.EnsureMergeRequestAsync(spec, token).ConfigureAwait(false);
            task.MergeRequestUrl = info.Url;
            task.MergeRequestIid = info.Iid;
            await EventAsync(task, "merge-request", $"Merge request {info.Url}").ConfigureAwait(false);
        }

        task.Status = AgentTaskStatus.AwaitingReview;
        await SaveAsync(task, token).ConfigureAwait(false);
        await EventAsync(task, "published", $"Awaiting review; verified: {verification}").ConfigureAwait(false);

        var message = new StringBuilder();
        message.Append(task.MergeRequestUrl is null ? $"Pushed branch `{branch}`" : $"Opened MR {task.MergeRequestUrl}");
        if (exhausted)
        {
            message.Append(" as a draft: the budget was exhausted (").Append(result.StopReason).Append(") before the work was finished.");
        }

        message.Append("\n\n").Append(result.Summary).Append("\n\nVerified: ").Append(verification);
        await NotifyAsync(task, Terminal(task, "published", message.ToString())).ConfigureAwait(false);
    }

    private async Task CancelledAsync(AgentTask task)
    {
        _logger.LogInformation("Task {Task} cancelled", task.DisplayRef);
        task.Status = AgentTaskStatus.Cancelled;
        await SaveAsync(task, CancellationToken.None).ConfigureAwait(false);
        await EventAsync(task, "cancelled", "Stopped by cancellation").ConfigureAwait(false);
        await NotifyAsync(task, Terminal(task, "cancelled", "Task cancelled.")).ConfigureAwait(false);
    }

    private async Task InterruptedAsync(AgentTask task)
    {
        _logger.LogInformation("Task {Task} interrupted by shutdown; re-queued", task.DisplayRef);
        task.Status = AgentTaskStatus.Queued;
        try
        {
            await SaveAsync(task, CancellationToken.None).ConfigureAwait(false);
            await EventAsync(task, "interrupted", "Worker shut down; task re-queued").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist interruption of task {Task}", task.DisplayRef);
        }
    }

    private async Task FailedAsync(AgentTask task, Exception ex)
    {
        _logger.LogError(ex, "Task {Task} failed", task.DisplayRef);
        task.Status = AgentTaskStatus.Failed;
        task.Error = TextUtil.TruncateEnd(ex.Message, 2000);
        try
        {
            await SaveAsync(task, CancellationToken.None).ConfigureAwait(false);
            await EventAsync(task, "failed", task.Error).ConfigureAwait(false);
            await NotifyAsync(task, Terminal(task, "failed", "Task failed: " + TextUtil.TruncateEnd(ex.Message, 1500))).ConfigureAwait(false);
        }
        catch (Exception inner)
        {
            _logger.LogError(inner, "Could not record failure of task {Task}", task.DisplayRef);
        }
    }

    /// <summary>Saves the task; on a concurrent modification reloads it and re-applies this run's fields.</summary>
    private async Task SaveAsync(AgentTask task, CancellationToken token)
    {
        task.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await _store.UpdateAsync(task, token).ConfigureAwait(false);
            return;
        }
        catch (TaskConcurrencyException)
        {
            _logger.LogDebug("Task {Task} changed concurrently; reloading", task.DisplayRef);
        }

        var fresh = await _store.GetAsync(task.Id, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Task #{task.Id} disappeared from the store.");

        if (fresh.Status == AgentTaskStatus.Cancelled && task.Status != AgentTaskStatus.Cancelled)
        {
            task.Version = fresh.Version;
            throw new OperationCanceledException("Task was cancelled.");
        }

        // Keep what other actors changed (pending follow-ups, MR links set by the channel) and apply ours on top.
        if (!string.IsNullOrWhiteSpace(fresh.PendingInstruction) && string.IsNullOrWhiteSpace(task.PendingInstruction))
        {
            task.PendingInstruction = fresh.PendingInstruction;
        }

        task.MergeRequestUrl ??= fresh.MergeRequestUrl;
        task.MergeRequestIid ??= fresh.MergeRequestIid;
        foreach (var (k, v) in fresh.Metadata)
        {
            task.Metadata.TryAdd(k, v);
        }

        task.Version = fresh.Version;
        await _store.UpdateAsync(task, token).ConfigureAwait(false);
    }

    private Task EventAsync(AgentTask task, string type, string message)
        => _store.AddEventAsync(new TaskEvent(task.Id, DateTimeOffset.UtcNow, type, TextUtil.TruncateEnd(message, 4000)), CancellationToken.None);

    private async Task NotifyAsync(AgentTask task, TaskNotification notification)
    {
        try
        {
            await _notifier.NotifyAsync(task, notification, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification {Key} for task {Task} failed", notification.Key, task.DisplayRef);
        }
    }

    /// <summary>
    /// A terminal notification for this attempt. The attempt number is part of the key so a retried task reports
    /// again, while a duplicate within one attempt (a worker restarted mid-publish, say) is posted once.
    /// </summary>
    internal static TaskNotification Terminal(AgentTask task, string kind, string markdown)
        => new($"{kind}:{task.Attempts.ToString(CultureInfo.InvariantCulture)}", markdown, Terminal: true);

    /// <summary>The event-log type for a coding action: <c>tool.write</c>, <c>tool.edit</c>, <c>tool.run</c>.</summary>
    internal static string ActionEventType(CodingActionKind kind) => "tool." + kind.ToString().ToLowerInvariant();

    internal static string PushedMessage(string branch, IReadOnlyList<string> changedFiles)
    {
        var sb = new StringBuilder();
        sb.Append("Pushed ").Append(branch).Append(" (").Append(changedFiles.Count).Append(" changed files)");
        if (changedFiles.Count == 0)
        {
            return sb.ToString();
        }

        sb.Append(": ").Append(string.Join(", ", changedFiles.Take(MaxListedFiles)));
        if (changedFiles.Count > MaxListedFiles)
        {
            sb.Append(" … (+").Append(changedFiles.Count - MaxListedFiles).Append(" more)");
        }

        return sb.ToString();
    }

    private static CallerIdentity Requester(AgentTask task)
    {
        // RequesterId is CallerIdentity.Key: "{Channel}:{ChannelUserId}". Roles are not persisted with the task;
        // a task only exists if the requester held Team, so that is what the tool registry sees.
        var channel = task.NotifyChannel;
        var userId = task.RequesterId;
        var colon = task.RequesterId.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && Enum.TryParse<Channel>(task.RequesterId[..colon], ignoreCase: true, out var parsed))
        {
            channel = parsed;
            userId = task.RequesterId[(colon + 1)..];
        }

        var roles = new HashSet<Role> { Role.Users, Role.Team };
        if (task.Metadata.TryGetValue("RequesterRoles", out var rolesText))
        {
            foreach (var part in rolesText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (RoleExtensions.TryParseRole(part, out var role))
                {
                    roles.Add(role);
                }
            }
        }

        return new CallerIdentity(channel, userId, task.RequesterUsername ?? task.RequesterName)
        {
            Roles = roles.Expand(),
            RolesResolved = true,
        };
    }

    internal static string CommitMessage(AgentTask task, CodingResult result, bool conventional)
    {
        var subject = TextUtil.FirstLine(result.Summary).Trim();
        if (string.IsNullOrEmpty(subject))
        {
            subject = task.Title ?? "agent changes";
        }

        var sb = new StringBuilder();
        if (conventional)
        {
            var type = ConventionalType(task, result);

            // Conventional Commits: lowercase imperative subject, no trailing period, subject line <= 72.
            subject = subject.TrimEnd('.');
            if (subject.Length > 0)
            {
                subject = char.ToLowerInvariant(subject[0]) + subject[1..];
            }

            subject = TextUtil.TruncateEnd(subject, Math.Max(10, 72 - type.Length - 2));
            sb.Append(type).Append(": ").Append(subject).Append("\n\n");
        }
        else
        {
            sb.Append(task.SourceRef).Append(": ").Append(TextUtil.TruncateEnd(subject, 72)).Append("\n\n");
        }

        sb.Append(result.Summary.Trim()).Append("\n\n");
        sb.Append("Requested-by: ").Append(task.RequesterName).Append('\n');
        sb.Append("Refs: ").Append(task.SourceRef).Append('\n');
        sb.Append("Task: #").Append(task.Id).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Guesses the Conventional Commits type from what was asked and what came back. It is a label on a
    /// commit a human is about to review, so a wrong guess is cosmetic; defaulting to <c>chore</c> is safe.
    /// </summary>
    internal static string ConventionalType(AgentTask task, CodingResult result)
    {
        // What was asked decides the type. The summary is only a fallback, because it describes what was
        // done and almost always says "added", which would make everything a feature.
        return Classify($"{task.Title} {task.Instruction}")
            ?? Classify(TextUtil.FirstLine(result.Summary))
            ?? "chore";
    }

    private static string? Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var lower = text.ToLowerInvariant();

        if (Mentions(lower, "fix", "bug", "broken", "regression", "crash", "error", "fails", "failing", "defect"))
        {
            return "fix";
        }

        if (Mentions(lower, "refactor", "rename", "restructure", "tidy", "clean up", "simplify"))
        {
            return "refactor";
        }

        if (Mentions(lower, "document", "documentation", "docs", "readme"))
        {
            return "docs";
        }

        if (Mentions(lower, "performance", "perf", "faster", "optimise", "optimize"))
        {
            return "perf";
        }

        if (Mentions(lower, "test", "tests", "coverage"))
        {
            return "test";
        }

        if (Mentions(lower, "add", "implement", "introduce", "support", "feature", "new "))
        {
            return "feat";
        }

        return null;
    }

    private static bool Mentions(string text, params string[] words)
        => words.Any(word => text.Contains(word, StringComparison.Ordinal));

    private static string MergeRequestDescription(AgentTask task, CodingResult result)
    {
        var sb = new StringBuilder(result.Summary.Trim());
        sb.Append("\n\n---\n");
        sb.Append("Verification: ").Append(result.Verified switch
        {
            true => "build/test passed",
            false => "build/test FAILED",
            null => "not run (no build/test command configured)",
        }).Append('\n');

        if (result.StopReason != CodingStopReason.Done)
        {
            sb.Append("Stopped early: ").Append(result.StopReason).Append('\n');
        }

        if (result.Verified == false && !string.IsNullOrWhiteSpace(result.VerificationOutput))
        {
            sb.Append("\n<details><summary>Verification output</summary>\n\n```\n")
              .Append(TextUtil.TruncateMiddle(result.VerificationOutput, 6000))
              .Append("\n```\n</details>\n");
        }

        if (result.CommandsRun.Count > 0)
        {
            sb.Append("\nCommands run: ").Append(result.CommandsRun.Count).Append('\n');
        }

        sb.Append("Budget used: ").Append(result.Turns).Append(" turns, ").Append(result.TokensUsed).Append(" tokens\n");
        sb.Append("Requested by: ").Append(task.RequesterName).Append(" (task #").Append(task.Id).Append(")\n");
        sb.Append(task.Source == TaskSource.GitLabIssue ? "Closes " : "Refs: ").Append(task.SourceRef).Append('\n');
        return sb.ToString();
    }

    /// <summary>Forwards at most one progress message per interval to the event log, without blocking the engine.</summary>
    private sealed class ThrottledProgress : IProgress<string>
    {
        private readonly Func<string, Task> _sink;
        private readonly TimeSpan _interval;
        private readonly Lock _lock = new();
        private DateTimeOffset _last = DateTimeOffset.MinValue;

        public ThrottledProgress(Func<string, Task> sink, TimeSpan interval)
        {
            _sink = sink;
            _interval = interval;
        }

        public void Report(string value)
        {
            lock (_lock)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - _last < _interval)
                {
                    return;
                }

                _last = now;
            }

            _ = _sink(value);
        }
    }

    /// <summary>
    /// Writes every coding action to the event log in the order it happened, off the engine's thread, and lets
    /// the runner wait for the last write. A failed write is logged and dropped: the trail is worth having, not
    /// worth failing the task over.
    /// </summary>
    private sealed class ActionLog : IProgress<CodingAction>
    {
        private readonly Func<CodingAction, Task> _sink;
        private readonly ILogger _logger;
        private readonly AgentTask _task;
        private readonly Lock _lock = new();
        private Task _tail = Task.CompletedTask;

        public ActionLog(Func<CodingAction, Task> sink, ILogger logger, AgentTask task)
        {
            _sink = sink;
            _logger = logger;
            _task = task;
        }

        public Task Completion
        {
            get
            {
                lock (_lock)
                {
                    return _tail;
                }
            }
        }

        public void Report(CodingAction value)
        {
            lock (_lock)
            {
                var previous = _tail;
                _tail = Task.Run(async () =>
                {
                    await previous.ConfigureAwait(false);
                    try
                    {
                        await _sink(value).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not record {Kind} action for task {Task}", value.Kind, _task.DisplayRef);
                    }
                });
            }
        }
    }
}
