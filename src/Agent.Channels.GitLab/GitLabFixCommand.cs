using System.Globalization;
using System.Text;
using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Commands;
using Agent.Core.Events;
using Agent.Core.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Channels.GitLab;

/// <summary>
/// <c>!fix</c> / <c>!implement</c>: starts a coding task from wherever the person happens to be. Without this a task
/// could only begin from a GitLab label or assignment, or a Jira label, so nobody in chat could ask for work.
/// </summary>
public sealed class GitLabFixCommand
{
    /// <summary>Enough of the instruction to serve as a task title when the target has none of its own.</summary>
    private const int TitleChars = 80;

    private const string Usage = """
        Tell me what to work on:
        - `!fix group/repo#123 [extra instruction]` — a GitLab issue; its title and description become the task
        - `!fix group/repo!45 [instruction]` — an open merge request
        - `!fix <issue or merge request URL> [instruction]`
        - `!fix group/repo <instruction>` — a repository, described in your own words

        Inside a GitLab issue or merge request, or on a Jira issue, the target may be left out: `!fix <instruction>`.
        """;

    private readonly IGitLabClient _client;
    private readonly ITaskService _tasks;
    private readonly ITaskStore _store;
    private readonly IOptionsMonitor<GitLabOptions> _options;
    private readonly ILogger<GitLabFixCommand> _logger;

    public GitLabFixCommand(
        IGitLabClient client,
        ITaskService tasks,
        ITaskStore store,
        IOptionsMonitor<GitLabOptions> options,
        ILogger<GitLabFixCommand> logger)
    {
        _client = client;
        _tasks = tasks;
        _store = store;
        _options = options;
        _logger = logger;
    }

    [Command(
        "fix",
        "Start a coding task from an issue, a merge request or a repository",
        Aliases = ["implement"],
        Role = Role.Team)]
    public async Task<CommandResult> FixAsync(
        CommandContext ctx,
        [Arg("group/repo#123, group/repo!45, a GitLab URL, or group/repo; omit inside an issue or merge request")] string? target = null,
        [Rest] string instruction = "",
        CancellationToken cancellationToken = default)
    {
        var extra = instruction;
        GitLabTarget? parsed = null;
        if (!string.IsNullOrWhiteSpace(target))
        {
            if (GitLabRefs.TryParseTarget(target, _options.CurrentValue.BaseUrl, out var explicitTarget))
            {
                parsed = explicitTarget;
            }
            else
            {
                // The first word was not a reference after all, so the whole line is the instruction.
                extra = ctx.RawArgs.Trim();
            }
        }

        if (parsed is not null)
        {
            return await StartFromTargetAsync(ctx, parsed, extra, cancellationToken).ConfigureAwait(false);
        }

        // A follow-up or task event already says which ticket the conversation is about.
        if (ctx.Event?.Task is { } context)
        {
            // The channel already knows a task owns this thread, even when its SourceRef is a different ticket.
            if (context.ExistingTaskId is { } owner
                && await _store.GetAsync(owner, cancellationToken).ConfigureAwait(false) is { IsActive: true } running)
            {
                return AlreadyRunning(running, context.SourceRef);
            }

            return GitLabRefs.TryParse(context.SourceRef, out var project, out var iid, out var type) && ctx.Channel == Channel.GitLab
                ? await StartFromTargetAsync(ctx, new GitLabTarget(project, type, iid), extra, cancellationToken).ConfigureAwait(false)
                : await StartFromContextAsync(ctx, context, extra, cancellationToken).ConfigureAwait(false);
        }

        // A plain question on a GitLab issue carries no task context, but the conversation id is the reference.
        if (ctx.Channel == Channel.GitLab && GitLabRefs.TryParse(ctx.ConversationId, out var path, out var ambientIid, out var ambientType))
        {
            return await StartFromTargetAsync(ctx, new GitLabTarget(path, ambientType, ambientIid), extra, cancellationToken).ConfigureAwait(false);
        }

        return CommandResult.Error(Usage);
    }

    /// <summary>Reads the issue or merge request so the ticket's own acceptance criteria become the instruction.</summary>
    private async Task<CommandResult> StartFromTargetAsync(CommandContext ctx, GitLabTarget target, string extra, CancellationToken cancellationToken)
    {
        var project = await _client.GetProjectAsync(target.Project, cancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            return CommandResult.Error($"I cannot see a GitLab project at `{target.Project}`.");
        }

        var projectId = project.Id.ToString(CultureInfo.InvariantCulture);
        var path = string.IsNullOrEmpty(project.PathWithNamespace) ? target.Project : project.PathWithNamespace;
        var caller = ctx.Caller.DisplayName;

        TaskRequest request;
        switch (target.Type)
        {
            case GitLabNoteableType.Issue:
            {
                var issue = await _client.GetIssueAsync(projectId, target.Iid, cancellationToken).ConfigureAwait(false);
                if (issue is null)
                {
                    return CommandResult.Error($"Issue `{GitLabRefs.Issue(path, target.Iid)}` was not found.");
                }

                request = Base(ctx, project, projectId) with
                {
                    Source = TaskSource.GitLabIssue,
                    SourceRef = GitLabRefs.Issue(path, issue.Iid),
                    SourceUrl = issue.WebUrl,
                    Title = issue.Title,
                    Instruction = Combine(issue.Title, issue.Description, extra, caller),
                };
                break;
            }

            case GitLabNoteableType.MergeRequest:
            {
                var mr = await _client.GetMergeRequestAsync(projectId, target.Iid, cancellationToken).ConfigureAwait(false);
                if (mr is null)
                {
                    return CommandResult.Error($"Merge request `{GitLabRefs.MergeRequest(path, target.Iid)}` was not found.");
                }

                request = Base(ctx, project, projectId) with
                {
                    Source = TaskSource.MergeRequest,
                    SourceRef = GitLabRefs.MergeRequest(path, mr.Iid),
                    SourceUrl = mr.WebUrl,
                    Title = mr.Title,
                    Instruction = Combine(mr.Title, mr.Description, extra, caller),
                    BaseBranch = mr.TargetBranch,
                    ExistingBranch = mr.SourceBranch,
                    MergeRequestIid = mr.Iid.ToString(CultureInfo.InvariantCulture),
                    MergeRequestUrl = mr.WebUrl,
                };
                break;
            }

            default:
            {
                if (string.IsNullOrWhiteSpace(extra))
                {
                    return CommandResult.Error($"Say what you want done in `{path}`, for example `!fix {path} make the nightly build green`.");
                }

                request = Base(ctx, project, projectId) with
                {
                    // A repository on its own has no ticket, so the task belongs to the conversation that asked.
                    Source = SourceFor(ctx.Channel),
                    SourceRef = ctx.ConversationId,
                    SourceUrl = project.WebUrl,
                    Title = Shorten(extra),
                    Instruction = extra.Trim(),
                };
                break;
            }
        }

        return await CreateAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A Jira issue (or any non-GitLab ticket) whose event already carries the repository and reference.</summary>
    private async Task<CommandResult> StartFromContextAsync(CommandContext ctx, TaskContext context, string extra, CancellationToken cancellationToken)
    {
        var instruction = Combine(context.Title ?? string.Empty, null, extra, ctx.Caller.DisplayName);
        if (string.IsNullOrWhiteSpace(instruction))
        {
            return CommandResult.Error(Usage);
        }

        var request = new TaskRequest
        {
            Source = context.Source,
            SourceRef = context.SourceRef,
            SourceUrl = context.SourceUrl,
            Title = context.Title ?? Shorten(instruction),
            Requester = ctx.Caller,
            Instruction = instruction,
            RepoUrl = context.RepoUrl,
            ProjectId = context.ProjectId,
            BaseBranch = context.BaseBranch,
            ExistingBranch = context.ExistingBranch,
            MergeRequestIid = context.MergeRequestIid,
            MergeRequestUrl = context.MergeRequestUrl,
            ConversationId = ctx.ConversationId,
            NotifyChannel = ctx.Channel,
        };

        return await CreateAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CommandResult> CreateAsync(TaskRequest request, CancellationToken cancellationToken)
    {
        var existing = await _store.FindActiveBySourceAsync(request.Source, request.SourceRef, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return AlreadyRunning(existing, request.SourceRef);
        }

        AgentTask task;
        try
        {
            task = await _tasks.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (RepositoryNotAllowedException ex)
        {
            return CommandResult.Error(ex.Message);
        }

        _logger.LogInformation("Task {Task} created by !fix from {Channel} ({Conversation})", task.DisplayRef, task.NotifyChannel, task.ConversationId);

        var sb = new StringBuilder();
        sb.Append("**Task #").Append(task.Id.ToString(CultureInfo.InvariantCulture)).Append("** — ").AppendLine(task.Title ?? request.SourceRef);
        sb.Append("Source `").Append(request.SourceRef).Append('`');
        if (!string.IsNullOrWhiteSpace(request.RepoUrl))
        {
            sb.Append(", branching from `").Append(request.BaseBranch ?? "the default branch").Append('`');
        }

        sb.AppendLine(".");
        sb.Append(task.Status == AgentTaskStatus.NeedsInput
            ? "I could not work out which repository to use — say `!fix group/repo <instruction>`."
            : "I will report progress here.");
        return CommandResult.Text(sb.ToString());
    }

    /// <summary>Two tasks on one ticket would fight over the same branch, so point at the one already running.</summary>
    private static CommandResult AlreadyRunning(AgentTask existing, string sourceRef)
    {
        var id = existing.Id.ToString(CultureInfo.InvariantCulture);
        var reference = existing.SourceRef.Length == 0 ? sourceRef : existing.SourceRef;
        return CommandResult.Error(
            $"Task **#{id}** is already working on `{reference}` ({existing.Status}). "
            + $"Comment there to add to it, or `!cancel {id}` first.");
    }

    /// <summary>The fields every GitLab-resolved request shares; the branches are overridden for a merge request.</summary>
    private static TaskRequest Base(CommandContext ctx, GitLabProject project, string projectId) => new()
    {
        Source = TaskSource.GitLabIssue,
        SourceRef = string.Empty,
        Requester = ctx.Caller,
        Instruction = string.Empty,
        RepoUrl = project.HttpUrlToRepo,
        ProjectId = projectId,
        BaseBranch = project.DefaultBranch,
        ConversationId = ctx.ConversationId,
        NotifyChannel = ctx.Channel,
    };

    /// <summary>Ticket text first, the caller's words last, so an explicit instruction wins in the coding prompt.</summary>
    private static string Combine(string title, string? description, string extra, string caller)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(title))
        {
            parts.Add(title.Trim());
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            parts.Add(description.Trim());
        }

        if (!string.IsNullOrWhiteSpace(extra))
        {
            parts.Add($"Additional instruction from {caller}: {extra.Trim()}");
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>Where a task belongs when the target is a bare repository: the conversation that asked for it.</summary>
    private static TaskSource SourceFor(Channel channel) => channel switch
    {
        Channel.Mattermost => TaskSource.Mattermost,
        Channel.Jira => TaskSource.JiraIssue,
        Channel.GitLab => TaskSource.GitLabIssue,
        _ => TaskSource.Cli,
    };

    private static string Shorten(string text)
    {
        var line = text.Trim().Split('\n')[0].Trim();
        return line.Length <= TitleChars ? line : line[..TitleChars].TrimEnd() + "…";
    }
}
