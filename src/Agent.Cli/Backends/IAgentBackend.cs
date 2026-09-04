namespace Agent.Cli.Backends;

public sealed record ChatResult(string Markdown, string ConversationId, string? Model, IReadOnlyList<string> ToolsUsed, bool IsCommand, bool IsError);

public sealed record TaskInfo(int Id, string Status, string Source, string SourceRef, string? Title, string? RepoUrl, string? WorkBranch, string? MergeRequestUrl, string? Summary, string? Error, int Turns, long TokensUsed, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string RequesterName);

public sealed record TaskEventInfo(DateTimeOffset At, string Type, string Message);

public sealed record WhoAmI(string Id, string? Username, string? Email, IReadOnlyList<string> Roles, IReadOnlyList<string> Groups);

/// <summary>What the CLI needs from the agent, whether it runs in-process or behind the host API.</summary>
public interface IAgentBackend
{
    string Description { get; }

    Task<ChatResult> ChatAsync(string text, string? conversationId, string? model, CancellationToken cancellationToken);

    IAsyncEnumerable<string> StreamAsync(string text, string conversationId, string? model, CancellationToken cancellationToken);

    Task<TaskInfo> CreateTaskAsync(string repoUrl, string instruction, string? title, string? baseBranch, CancellationToken cancellationToken);

    Task<IReadOnlyList<TaskInfo>> ListTasksAsync(bool all, int limit, CancellationToken cancellationToken);

    Task<TaskInfo?> GetTaskAsync(int id, CancellationToken cancellationToken);

    Task<IReadOnlyList<TaskEventInfo>> GetTaskEventsAsync(int id, CancellationToken cancellationToken);

    Task<TaskInfo?> CancelTaskAsync(int id, CancellationToken cancellationToken);

    Task<WhoAmI> WhoAmIAsync(CancellationToken cancellationToken);
}
