using Agent.Core.Channels;

namespace Agent.Core.Tasks;

public enum AgentTaskStatus
{
    Received,
    Queued,
    Preparing,
    Working,
    Verifying,
    Publishing,
    AwaitingReview,
    Done,
    Closed,
    Failed,
    NeedsInput,
    Cancelled,
    Interrupted,
}

public enum TaskSource
{
    GitLabIssue,
    JiraIssue,
    MergeRequest,
    Cli,
    Mattermost,
}

/// <summary>A coding task: one ticket → one branch → one merge request, plus follow-ups.</summary>
public sealed class AgentTask
{
    public int Id { get; set; }

    public TaskSource Source { get; set; }

    /// <summary>"PROJ-123", "group/repo#12", "group/repo!5", or a CLI-generated id.</summary>
    public string SourceRef { get; set; } = string.Empty;

    public string? SourceUrl { get; set; }

    public string? Title { get; set; }

    /// <summary><see cref="Authorization.CallerIdentity.Key"/> of the requester.</summary>
    public string RequesterId { get; set; } = string.Empty;

    public string RequesterName { get; set; } = string.Empty;

    /// <summary>Requester's username in the git hosting system, used to assign the MR reviewer.</summary>
    public string? RequesterUsername { get; set; }

    public Channel NotifyChannel { get; set; }

    /// <summary>Where progress is posted (thread root, issue key, MR ref).</summary>
    public string ConversationId { get; set; } = string.Empty;

    public string? RepoUrl { get; set; }

    public string? ProjectId { get; set; }

    public string? BaseBranch { get; set; }

    public string? WorkBranch { get; set; }

    public string? MergeRequestUrl { get; set; }

    public string? MergeRequestIid { get; set; }

    public AgentTaskStatus Status { get; set; } = AgentTaskStatus.Received;

    public string Instruction { get; set; } = string.Empty;

    /// <summary>Follow-up instructions that have not been worked on yet, newest last.</summary>
    public string? PendingInstruction { get; set; }

    public string? Summary { get; set; }

    public string? Error { get; set; }

    public int Turns { get; set; }

    public long TokensUsed { get; set; }

    public int Attempts { get; set; }

    public string? WorkerId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optimistic concurrency token maintained by the store.</summary>
    public long Version { get; set; }

    public bool IsActive => Status is not (AgentTaskStatus.Done or AgentTaskStatus.Closed or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled);

    public bool IsRunning => Status is AgentTaskStatus.Preparing or AgentTaskStatus.Working or AgentTaskStatus.Verifying or AgentTaskStatus.Publishing;

    public string DisplayRef => $"#{Id} ({SourceRef})";
}

public sealed record TaskEvent(int TaskId, DateTimeOffset At, string Type, string Message)
{
    public long Id { get; init; }
}

public sealed record TaskQuery
{
    public string? RequesterId { get; init; }

    public IReadOnlyCollection<AgentTaskStatus>? Statuses { get; init; }

    public bool ActiveOnly { get; init; }

    public int Limit { get; init; } = 20;
}

public sealed class TaskConcurrencyException : Exception
{
    public TaskConcurrencyException(int taskId)
        : base($"Task #{taskId} was modified concurrently.")
    {
    }
}
