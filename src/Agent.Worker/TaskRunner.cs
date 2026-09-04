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
        IMergeRequestPublisher? mergeRequests = null)
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
            await NotifyAsync(task, "I don't know which repository this task belongs to. Tell me the repository (or set it on the ticket) and I'll start.").ConfigureAwait(false);
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

        var run = new CodingRun(workspace, instruction, firstRun ? null : task.Summary, IsFollowUp: !firstRun, Requester(task));
        var progress = new ThrottledProgress(message => EventAsync(task, "progress", message), ProgressInterval);

        var result = await _engine.RunAsync(run, progress, token).ConfigureAwait(false);

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
            await NotifyAsync(task, noChanges.ToString()).ConfigureAwait(false);
            return;
        }

        var branch = task.WorkBranch ?? workspace.Branch;
        var credentials = _credentials?.GetCredentials(task.RepoUrl!);
        await _git.AddAllAsync(workspace.RepoPath, token).ConfigureAwait(false);
        await _git.CommitAsync(workspace.RepoPath, CommitMessage(task, result), options.CommitAuthorName, options.CommitAuthorEmail, token).ConfigureAwait(false);
        await _git.PushAsync(workspace.RepoPath, branch, credentials, setUpstream: true, token).ConfigureAwait(false);
        await EventAsync(task, "pushed", $"Pushed {branch} ({result.ChangedFiles.Count} changed files)").ConfigureAwait(false);

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
        await NotifyAsync(task, message.ToString()).ConfigureAwait(false);
    }

    private async Task CancelledAsync(AgentTask task)
    {
        _logger.LogInformation("Task {Task} cancelled", task.DisplayRef);
        task.Status = AgentTaskStatus.Cancelled;
        await SaveAsync(task, CancellationToken.None).ConfigureAwait(false);
        await EventAsync(task, "cancelled", "Stopped by cancellation").ConfigureAwait(false);
        await NotifyAsync(task, "Task cancelled.").ConfigureAwait(false);
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
            await NotifyAsync(task, "Task failed: " + TextUtil.TruncateEnd(ex.Message, 1500)).ConfigureAwait(false);
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

    private async Task NotifyAsync(AgentTask task, string markdown)
    {
        try
        {
            await _notifier.NotifyAsync(task, markdown, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification for task {Task} failed", task.DisplayRef);
        }
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

    private static string CommitMessage(AgentTask task, CodingResult result)
    {
        var title = TextUtil.TruncateEnd(TextUtil.FirstLine(result.Summary), 72);
        if (string.IsNullOrEmpty(title))
        {
            title = task.Title ?? "agent changes";
        }

        var sb = new StringBuilder();
        sb.Append(task.SourceRef).Append(": ").Append(title).Append("\n\n");
        sb.Append(result.Summary.Trim()).Append("\n\n");
        sb.Append("Requested-by: ").Append(task.RequesterName).Append('\n');
        sb.Append("Task: #").Append(task.Id).Append('\n');
        return sb.ToString();
    }

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
}
