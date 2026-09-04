using System.Text.RegularExpressions;
using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Events;
using Agent.Core.Tasks;

namespace Agent.Channels.Jira;

/// <summary>
/// Pure per-issue decision logic: which comments and labels on a polled issue become <see cref="InboundEvent"/>s.
/// Holds no state and touches no I/O so it can be unit tested with plain issue objects.
/// </summary>
public sealed class JiraEventMapper
{
    private readonly JiraOptions _options;
    private readonly string _botUsername;
    private readonly Regex _mention;

    public JiraEventMapper(JiraOptions options)
    {
        _options = options;
        _botUsername = options.EffectiveBotUsername;
        _mention = new Regex(@"\[~" + Regex.Escape(_botUsername) + @"\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>True when the issue carries the task label or has a mention comment after the cutoff. Cheap pre-check before task-store lookups.</summary>
    public bool IsRelevant(JiraIssue issue, DateTimeOffset cutoff)
        => HasTaskLabel(issue) || MentionComments(issue, cutoff).Any();

    public bool HasTaskLabel(JiraIssue issue)
        => !string.IsNullOrWhiteSpace(_options.TaskLabel)
           && issue.Fields.Labels.Any(l => string.Equals(l, _options.TaskLabel, StringComparison.OrdinalIgnoreCase));

    public bool IsBot(JiraUser? user)
        => user is not null
           && (string.Equals(user.Name, _botUsername, StringComparison.OrdinalIgnoreCase)
               || string.Equals(user.Key, _botUsername, StringComparison.OrdinalIgnoreCase));

    public bool MentionsBot(string? body) => body is not null && _mention.IsMatch(body);

    public string StripMention(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        var stripped = _mention.Replace(body, " ");
        stripped = Regex.Replace(stripped, @"[ \t]{2,}", " ");
        return string.Join('\n', stripped.Split('\n').Select(l => l.Trim())).Trim();
    }

    /// <summary>Comments created after the cutoff, not written by the bot, that mention the bot. Oldest first.</summary>
    public IEnumerable<JiraComment> MentionComments(JiraIssue issue, DateTimeOffset cutoff)
    {
        var comments = issue.Fields.Comment?.Comments;
        if (comments is null || comments.Count == 0)
        {
            return [];
        }

        return comments
            .Where(c => c.Created is { } created && created > cutoff)
            .Where(c => !IsBot(c.Author))
            .Where(c => MentionsBot(c.Body))
            .OrderBy(c => c.Created);
    }

    /// <summary>
    /// Maps one issue to events. <paramref name="activeTask"/> is the task already open for the issue, if any:
    /// with one, mentions become follow-ups and the label is ignored; without one, mentions are questions and the
    /// label is a task request. <paramref name="repoUrl"/> is attached to task requests.
    /// </summary>
    public IReadOnlyList<InboundEvent> Map(JiraIssue issue, DateTimeOffset cutoff, AgentTask? activeTask, string? repoUrl)
    {
        var events = new List<InboundEvent>();
        var fields = issue.Fields;
        var key = issue.Key;
        var project = fields.Project?.Key ?? JiraRepositoryResolver.ProjectKeyOf(key) ?? string.Empty;
        var url = _options.BrowseUrl(key);
        var summary = fields.Summary?.Trim() ?? string.Empty;

        if (activeTask is null && HasTaskLabel(issue))
        {
            var caller = ToCaller(fields.Reporter) ?? ToCaller(fields.Assignee);
            if (caller is not null)
            {
                var description = fields.Description?.Trim();
                events.Add(new InboundEvent
                {
                    Channel = Channel.Jira,
                    EventId = $"label:{key}:{_options.TaskLabel}",
                    Caller = caller,
                    ConversationId = key,
                    Text = string.IsNullOrEmpty(description) ? summary : $"{summary}\n\n{description}",
                    Kind = InboundKind.TaskRequest,
                    Timestamp = fields.Updated ?? fields.Created ?? DateTimeOffset.UtcNow,
                    Task = new TaskContext
                    {
                        Source = TaskSource.JiraIssue,
                        SourceRef = key,
                        SourceUrl = url,
                        Title = summary,
                        RepoUrl = repoUrl,
                        ProjectId = repoUrl is null ? null : JiraRepositoryResolver.ToTarget(repoUrl).ProjectId,
                    },
                    Metadata = Metadata(key, project, null),
                });
            }
        }

        foreach (var comment in MentionComments(issue, cutoff))
        {
            var caller = ToCaller(comment.Author);
            if (caller is null)
            {
                continue;
            }

            events.Add(new InboundEvent
            {
                Channel = Channel.Jira,
                EventId = $"comment:{comment.Id}",
                Caller = caller,
                ConversationId = key,
                Text = StripMention(comment.Body),
                Kind = activeTask is null ? InboundKind.Message : InboundKind.FollowUp,
                Timestamp = comment.Created ?? fields.Updated ?? DateTimeOffset.UtcNow,
                Task = activeTask is null
                    ? null
                    : new TaskContext
                    {
                        Source = TaskSource.JiraIssue,
                        SourceRef = key,
                        SourceUrl = url,
                        Title = summary,
                        ExistingTaskId = activeTask.Id,
                        RepoUrl = activeTask.RepoUrl,
                        ProjectId = activeTask.ProjectId,
                        BaseBranch = activeTask.BaseBranch,
                        ExistingBranch = activeTask.WorkBranch,
                        MergeRequestUrl = activeTask.MergeRequestUrl,
                        MergeRequestIid = activeTask.MergeRequestIid,
                    },
                Metadata = Metadata(key, project, comment.Id),
            });
        }

        return events;
    }

    public static CallerIdentity? ToCaller(JiraUser? user)
    {
        var id = user?.Login;
        if (user is null || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return new CallerIdentity(Channel.Jira, id, user.Name ?? user.Key, NullIfEmpty(user.EmailAddress));
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static Dictionary<string, string> Metadata(string key, string project, string? commentId)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["issue"] = key,
            ["project"] = project,
        };

        if (commentId is not null)
        {
            metadata["comment_id"] = commentId;
        }

        return metadata;
    }
}
