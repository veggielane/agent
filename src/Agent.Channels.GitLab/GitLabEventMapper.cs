using System.Globalization;
using System.Text.RegularExpressions;
using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Events;
using Agent.Core.Tasks;

namespace Agent.Channels.GitLab;

/// <summary>What the poller should do with a to-do.</summary>
public enum TodoDisposition
{
    /// <summary>Mark done and forget (build failures, approvals, the bot's own activity, unsupported targets).</summary>
    Ignore,

    /// <summary>A note mentioning the bot on an issue or merge request.</summary>
    Mention,

    /// <summary>An issue assigned to the bot.</summary>
    Assignment,
}

/// <summary>Facts the poller gathered for a mention to-do; null members mean the lookup found nothing or was skipped.</summary>
public sealed record MentionInput(
    GitLabTodo Todo,
    GitLabUser? AuthorDetails,
    GitLabProject? Project,
    AgentTask? ExistingTask,
    GitLabMergeRequest? MergeRequest,
    string? DiscussionId,
    GitLabNotePosition? Position);

public sealed record AssignmentInput(GitLabTodo Todo, GitLabProject Project, GitLabUser? AuthorDetails);

/// <summary>Pure decision logic: GitLab payloads in, <see cref="InboundEvent"/>s out. No I/O.</summary>
public sealed partial class GitLabEventMapper
{
    public const string ProjectIdKey = "project_id";
    public const string ProjectPathKey = "project_path";
    public const string IidKey = "iid";
    public const string TargetTypeKey = "target_type";
    public const string NoteIdKey = "note_id";
    public const string DiscussionIdKey = "discussion_id";
    public const string TodoIdKey = "todo_id";

    private readonly GitLabOptions _options;

    public GitLabEventMapper(GitLabOptions options)
    {
        _options = options;
    }

    public TodoDisposition Classify(GitLabTodo todo)
    {
        if (todo.Project is null || todo.Target is null || todo.Author is null)
        {
            return TodoDisposition.Ignore;
        }

        if (!string.IsNullOrWhiteSpace(_options.BotUsername) && string.Equals(todo.Author.Username, _options.BotUsername, StringComparison.OrdinalIgnoreCase))
        {
            return TodoDisposition.Ignore;
        }

        if (!GitLabNoteableTypeExtensions.TryParse(todo.TargetType, out var type))
        {
            return TodoDisposition.Ignore;
        }

        return todo.ActionName.ToLowerInvariant() switch
        {
            "mentioned" or "directly_addressed" => TodoDisposition.Mention,
            "assigned" when type == GitLabNoteableType.Issue && _options.TaskOnAssign => TodoDisposition.Assignment,
            _ => TodoDisposition.Ignore,
        };
    }

    /// <summary>
    /// Issue mention → Message, or FollowUp when the issue has an active task. MR mention → FollowUp on the agent's
    /// MR (or on any MR when <see cref="GitLabOptions.FollowUpsOnForeignMrs"/>), else Message.
    /// </summary>
    public InboundEvent MapMention(MentionInput input)
    {
        var todo = input.Todo;
        var project = todo.Project ?? throw new ArgumentException("To-do has no project.", nameof(input));
        var target = todo.Target ?? throw new ArgumentException("To-do has no target.", nameof(input));
        var author = todo.Author ?? throw new ArgumentException("To-do has no author.", nameof(input));
        if (!GitLabNoteableTypeExtensions.TryParse(todo.TargetType, out var type))
        {
            throw new ArgumentException($"Unsupported target type '{todo.TargetType}'.", nameof(input));
        }

        var projectId = project.Id.ToString(CultureInfo.InvariantCulture);
        var path = project.PathWithNamespace;
        var noteId = ParseNoteId(todo.TargetUrl);
        var text = StripBotMention(todo.Body, _options.BotUsername);
        if (input.Position is { } position && (position.NewPath ?? position.OldPath) is { } file)
        {
            var line = position.NewLine ?? position.OldLine;
            text = $"{text}\n\n(Comment on {file}{(line is null ? string.Empty : $" line {line.Value.ToString(CultureInfo.InvariantCulture)}")})";
        }

        var metadata = Metadata(projectId, path, target.Iid, type, noteId, input.DiscussionId, todo.Id);
        var sourceRef = GitLabRefs.Build(type, path, target.Iid);
        var evt = new InboundEvent
        {
            Channel = Channel.GitLab,
            EventId = TodoEventId(todo.Id),
            Caller = ToCaller(author, input.AuthorDetails),
            ConversationId = sourceRef,
            Text = text,
            Kind = InboundKind.Message,
            Timestamp = todo.CreatedAt == default ? DateTimeOffset.UtcNow : todo.CreatedAt,
            Metadata = metadata,
        };

        if (type == GitLabNoteableType.Issue)
        {
            if (input.ExistingTask is not { } task)
            {
                return evt;
            }

            return evt with
            {
                Kind = InboundKind.FollowUp,
                Task = new TaskContext
                {
                    Source = TaskSource.GitLabIssue,
                    SourceRef = sourceRef,
                    SourceUrl = target.WebUrl,
                    ExistingTaskId = task.Id,
                    ProjectId = task.ProjectId ?? projectId,
                    RepoUrl = task.RepoUrl ?? input.Project?.HttpUrlToRepo,
                    BaseBranch = task.BaseBranch ?? input.Project?.DefaultBranch,
                    ExistingBranch = task.WorkBranch,
                    MergeRequestIid = task.MergeRequestIid,
                    MergeRequestUrl = task.MergeRequestUrl,
                    Title = target.Title,
                },
            };
        }

        var sourceBranch = target.SourceBranch ?? input.MergeRequest?.SourceBranch ?? input.ExistingTask?.WorkBranch;
        var targetBranch = target.TargetBranch ?? input.MergeRequest?.TargetBranch ?? input.ExistingTask?.BaseBranch;
        var webUrl = target.WebUrl ?? input.MergeRequest?.WebUrl ?? StripAnchor(todo.TargetUrl);
        var context = new TaskContext
        {
            Source = TaskSource.MergeRequest,
            SourceRef = sourceRef,
            SourceUrl = webUrl,
            ProjectId = projectId,
            RepoUrl = input.ExistingTask?.RepoUrl ?? input.Project?.HttpUrlToRepo,
            BaseBranch = targetBranch,
            ExistingBranch = sourceBranch,
            MergeRequestIid = target.Iid.ToString(CultureInfo.InvariantCulture),
            MergeRequestUrl = webUrl,
            Title = target.Title ?? input.MergeRequest?.Title,
        };

        if (input.ExistingTask is { } owner)
        {
            return evt with { Kind = InboundKind.FollowUp, Task = context with { ExistingTaskId = owner.Id } };
        }

        if (_options.FollowUpsOnForeignMrs)
        {
            return evt with { Kind = InboundKind.FollowUp, Task = context };
        }

        return evt;
    }

    /// <summary>An issue assigned to the bot becomes a task request; the assigner is the requester.</summary>
    public InboundEvent MapAssignment(AssignmentInput input)
    {
        var todo = input.Todo;
        var target = todo.Target ?? throw new ArgumentException("To-do has no target.", nameof(input));
        var author = todo.Author ?? throw new ArgumentException("To-do has no author.", nameof(input));
        var project = input.Project;
        var projectId = project.Id.ToString(CultureInfo.InvariantCulture);
        var path = string.IsNullOrEmpty(project.PathWithNamespace) ? todo.Project?.PathWithNamespace ?? projectId : project.PathWithNamespace;
        var title = target.Title ?? todo.Body ?? string.Empty;

        return BuildTaskRequest(
            eventId: TodoEventId(todo.Id),
            caller: ToCaller(author, input.AuthorDetails),
            project: project,
            projectId: projectId,
            path: path,
            iid: target.Iid,
            title: title,
            description: target.Description,
            webUrl: target.WebUrl ?? StripAnchor(todo.TargetUrl),
            timestamp: todo.CreatedAt == default ? DateTimeOffset.UtcNow : todo.CreatedAt,
            todoId: todo.Id);
    }

    /// <summary>An issue carrying the task label becomes a task request; the issue author is the requester.</summary>
    public InboundEvent MapLabelledIssue(GitLabIssue issue, GitLabProject project, GitLabUser? authorDetails)
    {
        var author = issue.Author ?? throw new ArgumentException("Issue has no author.", nameof(issue));
        var projectId = project.Id.ToString(CultureInfo.InvariantCulture);
        return BuildTaskRequest(
            eventId: LabelEventId(issue.ProjectId, issue.Iid),
            caller: ToCaller(author, authorDetails),
            project: project,
            projectId: projectId,
            path: project.PathWithNamespace,
            iid: issue.Iid,
            title: issue.Title,
            description: issue.Description,
            webUrl: issue.WebUrl,
            timestamp: issue.UpdatedAt == default ? DateTimeOffset.UtcNow : issue.UpdatedAt,
            todoId: null);
    }

    public static string TodoEventId(long todoId) => $"todo:{todoId.ToString(CultureInfo.InvariantCulture)}";

    public static string LabelEventId(long projectId, long iid) => $"label:{projectId.ToString(CultureInfo.InvariantCulture)}:{iid.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Extracts the note id from a <c>...#note_123</c> target URL.</summary>
    public static long? ParseNoteId(string? targetUrl)
    {
        if (string.IsNullOrEmpty(targetUrl))
        {
            return null;
        }

        var match = NoteAnchor().Match(targetUrl);
        return match.Success && long.TryParse(match.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    /// <summary>Removes <c>@botUsername</c> mentions and tidies whitespace.</summary>
    public static string StripBotMention(string? text, string botUsername)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var result = text;
        if (!string.IsNullOrWhiteSpace(botUsername))
        {
            result = Regex.Replace(result, $@"(?<![\w.-])@{Regex.Escape(botUsername)}(?![\w-])", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        result = Regex.Replace(result, @"[ \t]{2,}", " ");
        return result.Trim();
    }

    /// <summary>Identity from the compact author plus, when known, the full user (email and LDAP DN).</summary>
    public static CallerIdentity ToCaller(GitLabUserRef author, GitLabUser? details)
    {
        var ldapDn = details?.Identities?
            .FirstOrDefault(i => i.Provider.StartsWith("ldap", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(i.ExternUid))?
            .ExternUid;

        return new CallerIdentity(
            Channel.GitLab,
            author.Id.ToString(CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(author.Username) ? details?.Username : author.Username,
            string.IsNullOrWhiteSpace(details?.Email) ? null : details.Email,
            LdapDn: ldapDn);
    }

    internal static Dictionary<string, string> Metadata(string projectId, string path, long iid, GitLabNoteableType type, long? noteId, string? discussionId, long? todoId)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ProjectIdKey] = projectId,
            [ProjectPathKey] = path,
            [IidKey] = iid.ToString(CultureInfo.InvariantCulture),
            [TargetTypeKey] = type.ToTargetType(),
        };

        if (noteId is not null)
        {
            metadata[NoteIdKey] = noteId.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrEmpty(discussionId))
        {
            metadata[DiscussionIdKey] = discussionId;
        }

        if (todoId is not null)
        {
            metadata[TodoIdKey] = todoId.Value.ToString(CultureInfo.InvariantCulture);
        }

        return metadata;
    }

    private static InboundEvent BuildTaskRequest(string eventId, CallerIdentity caller, GitLabProject project, string projectId, string path, long iid, string title, string? description, string? webUrl, DateTimeOffset timestamp, long? todoId)
    {
        var sourceRef = GitLabRefs.Issue(path, iid);
        var text = string.IsNullOrWhiteSpace(description) ? title.Trim() : $"{title.Trim()}\n\n{description.Trim()}";
        return new InboundEvent
        {
            Channel = Channel.GitLab,
            EventId = eventId,
            Caller = caller,
            ConversationId = sourceRef,
            Text = text,
            Kind = InboundKind.TaskRequest,
            Timestamp = timestamp,
            Metadata = Metadata(projectId, path, iid, GitLabNoteableType.Issue, null, null, todoId),
            Task = new TaskContext
            {
                Source = TaskSource.GitLabIssue,
                SourceRef = sourceRef,
                SourceUrl = webUrl,
                ProjectId = projectId,
                Title = title,
                RepoUrl = project.HttpUrlToRepo,
                BaseBranch = project.DefaultBranch,
            },
        };
    }

    private static string? StripAnchor(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var hash = url.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? url : url[..hash];
    }

    [GeneratedRegex(@"#note_(?<id>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex NoteAnchor();
}
