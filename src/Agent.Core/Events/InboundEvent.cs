using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Tasks;

namespace Agent.Core.Events;

public enum InboundKind
{
    /// <summary>A question or a !command.</summary>
    Message,

    /// <summary>A request to start a coding task (labelled / assigned issue, CLI task create, ...).</summary>
    TaskRequest,

    /// <summary>An instruction for an existing task (mention on the agent's MR, comment on a task's issue).</summary>
    FollowUp,
}

/// <summary>
/// One thing a channel wants the agent to look at. Channel adapters produce these; the pipeline consumes them.
/// <see cref="Text"/> has the bot mention already removed.
/// </summary>
public sealed record InboundEvent
{
    public required Channel Channel { get; init; }

    /// <summary>Idempotency key, unique per channel (post id, comment id, note id, todo id...).</summary>
    public required string EventId { get; init; }

    public required CallerIdentity Caller { get; init; }

    /// <summary>Groups messages that share history: a Mattermost thread root, a Jira issue key, a GitLab issue/MR.</summary>
    public required string ConversationId { get; init; }

    public required string Text { get; init; }

    public InboundKind Kind { get; init; } = InboundKind.Message;

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>True for DMs and the CLI: denials are always answered, and replies are not threaded.</summary>
    public bool IsPrivate { get; init; }

    /// <summary>Set for <see cref="InboundKind.TaskRequest"/> and <see cref="InboundKind.FollowUp"/>.</summary>
    public TaskContext? Task { get; init; }

    /// <summary>Channel-specific reply routing data (post id, root id, issue key, project id, MR iid, ...).</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    public string? Meta(string key) => Metadata.TryGetValue(key, out var value) ? value : null;
}

/// <summary>What the task pipeline needs to know that the ticket text alone does not say.</summary>
public sealed record TaskContext
{
    public required TaskSource Source { get; init; }

    /// <summary>"PROJ-123", "group/repo#12", "group/repo!5".</summary>
    public required string SourceRef { get; init; }

    public string? SourceUrl { get; init; }

    public string? RepoUrl { get; init; }

    /// <summary>GitLab project id or path, when known.</summary>
    public string? ProjectId { get; init; }

    public string? BaseBranch { get; init; }

    /// <summary>For follow-ups on an existing MR: the branch to continue on.</summary>
    public string? ExistingBranch { get; init; }

    /// <summary>For follow-ups: the task that owns the MR, when the channel already knows it.</summary>
    public int? ExistingTaskId { get; init; }

    public string? MergeRequestUrl { get; init; }

    public string? MergeRequestIid { get; init; }

    public string? Title { get; init; }
}
