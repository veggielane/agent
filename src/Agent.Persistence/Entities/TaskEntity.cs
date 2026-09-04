using Agent.Core.Channels;
using Agent.Core.Tasks;

namespace Agent.Persistence.Entities;

/// <summary>
/// Row in <c>Tasks</c>; mirrors <see cref="AgentTask"/>. Timestamps are stored as UTC <see cref="DateTime"/>
/// so SQLite can order and compare them (it cannot do either with <see cref="DateTimeOffset"/>).
/// </summary>
public sealed class TaskEntity
{
    public int Id { get; set; }

    public TaskSource Source { get; set; }

    public string SourceRef { get; set; } = string.Empty;

    public string? SourceUrl { get; set; }

    public string? Title { get; set; }

    public string RequesterId { get; set; } = string.Empty;

    public string RequesterName { get; set; } = string.Empty;

    public string? RequesterUsername { get; set; }

    public Channel NotifyChannel { get; set; }

    public string ConversationId { get; set; } = string.Empty;

    public string? RepoUrl { get; set; }

    public string? ProjectId { get; set; }

    public string? BaseBranch { get; set; }

    public string? WorkBranch { get; set; }

    public string? MergeRequestUrl { get; set; }

    public string? MergeRequestIid { get; set; }

    public AgentTaskStatus Status { get; set; }

    public string Instruction { get; set; } = string.Empty;

    public string? PendingInstruction { get; set; }

    public string? Summary { get; set; }

    public string? Error { get; set; }

    public int Turns { get; set; }

    public long TokensUsed { get; set; }

    public int Attempts { get; set; }

    public string? WorkerId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary><see cref="AgentTask.Metadata"/> serialised as a JSON object.</summary>
    public string MetadataJson { get; set; } = "{}";

    /// <summary>Optimistic concurrency token, incremented by the store on every write.</summary>
    public long Version { get; set; }
}
