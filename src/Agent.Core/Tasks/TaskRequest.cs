using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Events;

namespace Agent.Core.Tasks;

public sealed record TaskRequest
{
    public required TaskSource Source { get; init; }

    public required string SourceRef { get; init; }

    public string? SourceUrl { get; init; }

    public string? Title { get; init; }

    public required CallerIdentity Requester { get; init; }

    public required string Instruction { get; init; }

    public string? RepoUrl { get; init; }

    public string? ProjectId { get; init; }

    public string? BaseBranch { get; init; }

    public string? ExistingBranch { get; init; }

    public string? MergeRequestUrl { get; init; }

    public string? MergeRequestIid { get; init; }

    public required string ConversationId { get; init; }

    public required Channel NotifyChannel { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public static TaskRequest From(InboundEvent evt, CallerIdentity caller)
    {
        var task = evt.Task ?? throw new ArgumentException("Event carries no task context.", nameof(evt));
        return new TaskRequest
        {
            Source = task.Source,
            SourceRef = task.SourceRef,
            SourceUrl = task.SourceUrl,
            Title = task.Title,
            Requester = caller,
            Instruction = evt.Text,
            RepoUrl = task.RepoUrl,
            ProjectId = task.ProjectId,
            BaseBranch = task.BaseBranch,
            ExistingBranch = task.ExistingBranch,
            MergeRequestUrl = task.MergeRequestUrl,
            MergeRequestIid = task.MergeRequestIid,
            ConversationId = evt.ConversationId,
            NotifyChannel = evt.Channel,
            Metadata = evt.Metadata,
        };
    }
}
