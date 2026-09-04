using System.Text.Json;
using Agent.Core.Tasks;
using Agent.Persistence.Entities;

namespace Agent.Persistence.Stores;

/// <summary>Copies between <see cref="AgentTask"/> and <see cref="TaskEntity"/>.</summary>
internal static class TaskMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    public static AgentTask ToModel(TaskEntity e) => new()
    {
        Id = e.Id,
        Source = e.Source,
        SourceRef = e.SourceRef,
        SourceUrl = e.SourceUrl,
        Title = e.Title,
        RequesterId = e.RequesterId,
        RequesterName = e.RequesterName,
        RequesterUsername = e.RequesterUsername,
        NotifyChannel = e.NotifyChannel,
        ConversationId = e.ConversationId,
        RepoUrl = e.RepoUrl,
        ProjectId = e.ProjectId,
        BaseBranch = e.BaseBranch,
        WorkBranch = e.WorkBranch,
        MergeRequestUrl = e.MergeRequestUrl,
        MergeRequestIid = e.MergeRequestIid,
        Status = e.Status,
        Instruction = e.Instruction,
        PendingInstruction = e.PendingInstruction,
        Summary = e.Summary,
        Error = e.Error,
        Turns = e.Turns,
        TokensUsed = e.TokensUsed,
        Attempts = e.Attempts,
        WorkerId = e.WorkerId,
        CreatedAt = ToOffset(e.CreatedAt),
        UpdatedAt = ToOffset(e.UpdatedAt),
        Metadata = ParseMetadata(e.MetadataJson),
        Version = e.Version,
    };

    public static TaskEntity ToEntity(AgentTask t)
    {
        var e = new TaskEntity
        {
            Id = t.Id,
            CreatedAt = ToUtc(t.CreatedAt),
            UpdatedAt = ToUtc(t.UpdatedAt),
            Version = t.Version,
        };
        Apply(t, e);
        return e;
    }

    /// <summary>Copies the mutable fields onto a tracked entity; identity, timestamps and version are left to the store.</summary>
    public static void Apply(AgentTask t, TaskEntity e)
    {
        e.Source = t.Source;
        e.SourceRef = t.SourceRef;
        e.SourceUrl = t.SourceUrl;
        e.Title = t.Title;
        e.RequesterId = t.RequesterId;
        e.RequesterName = t.RequesterName;
        e.RequesterUsername = t.RequesterUsername;
        e.NotifyChannel = t.NotifyChannel;
        e.ConversationId = t.ConversationId;
        e.RepoUrl = t.RepoUrl;
        e.ProjectId = t.ProjectId;
        e.BaseBranch = t.BaseBranch;
        e.WorkBranch = t.WorkBranch;
        e.MergeRequestUrl = t.MergeRequestUrl;
        e.MergeRequestIid = t.MergeRequestIid;
        e.Status = t.Status;
        e.Instruction = t.Instruction;
        e.PendingInstruction = t.PendingInstruction;
        e.Summary = t.Summary;
        e.Error = t.Error;
        e.Turns = t.Turns;
        e.TokensUsed = t.TokensUsed;
        e.Attempts = t.Attempts;
        e.WorkerId = t.WorkerId;
        e.MetadataJson = SerializeMetadata(t.Metadata);
    }

    public static TaskEvent ToModel(TaskEventEntity e) => new(e.TaskId, ToOffset(e.At), e.Type, e.Message) { Id = e.Id };

    public static TaskEventEntity ToEntity(TaskEvent evt) => new()
    {
        TaskId = evt.TaskId,
        At = ToUtc(evt.At),
        Type = evt.Type,
        Message = evt.Message,
    };

    public static DateTimeOffset ToOffset(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    public static DateTime ToUtc(DateTimeOffset value) => value.UtcDateTime;

    private static string SerializeMetadata(Dictionary<string, string> metadata)
        => metadata.Count == 0 ? "{}" : JsonSerializer.Serialize(metadata, JsonOptions);

    private static Dictionary<string, string> ParseMetadata(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
        if (parsed is not null)
        {
            foreach (var (key, value) in parsed)
            {
                result[key] = value;
            }
        }

        return result;
    }
}
