namespace Agent.Channels.GitLab;

// GitLab REST API v4 shapes (snake_case on the wire; see GitLabJson). Only the fields the channel uses.

/// <summary>The compact user object embedded as <c>author</c> / <c>assignee</c>.</summary>
public sealed record GitLabUserRef
{
    public long Id { get; init; }

    public string Username { get; init; } = string.Empty;

    public string? Name { get; init; }

    public string? State { get; init; }

    public string? WebUrl { get; init; }
}

public sealed record GitLabIdentity
{
    public string Provider { get; init; } = string.Empty;

    public string? ExternUid { get; init; }
}

/// <summary>GET /users/:id. <see cref="Email"/> is null unless the token has admin scope or the user made it public.</summary>
public sealed record GitLabUser
{
    public long Id { get; init; }

    public string Username { get; init; } = string.Empty;

    public string? Name { get; init; }

    public string? Email { get; init; }

    public string? State { get; init; }

    public bool? Bot { get; init; }

    public IReadOnlyList<GitLabIdentity>? Identities { get; init; }
}

/// <summary>The compact project object embedded in a to-do.</summary>
public sealed record GitLabProjectRef
{
    public long Id { get; init; }

    public string? Name { get; init; }

    public string? Path { get; init; }

    public string PathWithNamespace { get; init; } = string.Empty;
}

/// <summary>GET /projects/:id.</summary>
public sealed record GitLabProject
{
    public long Id { get; init; }

    public string? Name { get; init; }

    public string? Path { get; init; }

    public string PathWithNamespace { get; init; } = string.Empty;

    public string? DefaultBranch { get; init; }

    public string? HttpUrlToRepo { get; init; }

    public string? WebUrl { get; init; }
}

public sealed record GitLabReferences
{
    public string? Short { get; init; }

    public string? Relative { get; init; }

    public string? Full { get; init; }
}

/// <summary>The issue or merge request a to-do points at (a subset of both shapes).</summary>
public sealed record GitLabTodoTarget
{
    public long Id { get; init; }

    public long Iid { get; init; }

    public long ProjectId { get; init; }

    public string? Title { get; init; }

    public string? Description { get; init; }

    public string? State { get; init; }

    public GitLabUserRef? Author { get; init; }

    public IReadOnlyList<string>? Labels { get; init; }

    public string? SourceBranch { get; init; }

    public string? TargetBranch { get; init; }

    public string? WebUrl { get; init; }
}

/// <summary>GET /todos. <see cref="Body"/> is the note text for mentions and the target title for assignments.</summary>
public sealed record GitLabTodo
{
    public long Id { get; init; }

    public GitLabProjectRef? Project { get; init; }

    public GitLabUserRef? Author { get; init; }

    /// <summary>assigned, mentioned, directly_addressed, marked, build_failed, approval_required, review_requested, ...</summary>
    public string ActionName { get; init; } = string.Empty;

    /// <summary>Issue, MergeRequest, DesignManagement::Design, AlertManagement::Alert, ...</summary>
    public string TargetType { get; init; } = string.Empty;

    public GitLabTodoTarget? Target { get; init; }

    /// <summary>Web URL of the target; for note-triggered to-dos it carries a <c>#note_&lt;id&gt;</c> anchor.</summary>
    public string? TargetUrl { get; init; }

    public string? Body { get; init; }

    public string? State { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record GitLabIssue
{
    public long Id { get; init; }

    public long Iid { get; init; }

    public long ProjectId { get; init; }

    public string Title { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string State { get; init; } = string.Empty;

    public GitLabUserRef? Author { get; init; }

    public IReadOnlyList<string>? Labels { get; init; }

    public string? WebUrl { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public GitLabReferences? References { get; init; }
}

/// <summary>Diff position of a note in a merge request discussion.</summary>
public sealed record GitLabNotePosition
{
    public string? OldPath { get; init; }

    public string? NewPath { get; init; }

    public int? OldLine { get; init; }

    public int? NewLine { get; init; }

    public string? PositionType { get; init; }
}

public sealed record GitLabNote
{
    public long Id { get; init; }

    /// <summary>DiffNote, DiscussionNote, or null for plain notes.</summary>
    public string? Type { get; init; }

    public string Body { get; init; } = string.Empty;

    public GitLabUserRef? Author { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>True for GitLab-generated notes ("added 1 commit", "changed the description").</summary>
    public bool System { get; init; }

    public long? NoteableId { get; init; }

    public string? NoteableType { get; init; }

    public long? NoteableIid { get; init; }

    public bool? Resolvable { get; init; }

    public GitLabNotePosition? Position { get; init; }
}

public sealed record GitLabDiscussion
{
    public string Id { get; init; } = string.Empty;

    public bool IndividualNote { get; init; }

    public IReadOnlyList<GitLabNote> Notes { get; init; } = [];
}

/// <summary>
/// A CI pipeline, as embedded in <see cref="GitLabMergeRequest.HeadPipeline"/>. <see cref="Status"/> is one of
/// created, waiting_for_resource, preparing, pending, running, success, failed, canceled, skipped, manual, scheduled.
/// </summary>
public sealed record GitLabPipeline
{
    public long Id { get; init; }

    public long? ProjectId { get; init; }

    public string Status { get; init; } = string.Empty;

    public string? Ref { get; init; }

    public string? Sha { get; init; }

    public string? WebUrl { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public bool IsFailed => string.Equals(Status, "failed", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One job of GET /projects/:id/pipelines/:pipeline_id/jobs.</summary>
public sealed record GitLabJob
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Stage { get; init; }

    /// <summary>created, pending, running, failed, success, canceled, skipped, manual.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>True when the job is allowed to fail without failing the pipeline, so its failure is not the agent's problem.</summary>
    public bool AllowFailure { get; init; }

    public string? FailureReason { get; init; }

    public string? WebUrl { get; init; }

    public bool IsFailed => string.Equals(Status, "failed", StringComparison.OrdinalIgnoreCase);
}

public sealed record GitLabMergeRequest
{
    public long Id { get; init; }

    public long Iid { get; init; }

    public long ProjectId { get; init; }

    public string Title { get; init; } = string.Empty;

    public string? Description { get; init; }

    /// <summary>opened, closed, locked, merged.</summary>
    public string State { get; init; } = string.Empty;

    public string SourceBranch { get; init; } = string.Empty;

    public string TargetBranch { get; init; } = string.Empty;

    public string? WebUrl { get; init; }

    public GitLabUserRef? Author { get; init; }

    public IReadOnlyList<string>? Labels { get; init; }

    public DateTimeOffset? MergedAt { get; init; }

    /// <summary>The pipeline of the latest commit on the source branch. Null when the project has no CI or nothing ran yet.</summary>
    public GitLabPipeline? HeadPipeline { get; init; }

    public bool Draft { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>One hit of GET /projects/:id/search?scope=blobs.</summary>
public sealed record GitLabBlob
{
    public string? Basename { get; init; }

    public string? Data { get; init; }

    public string? Path { get; init; }

    public string? Filename { get; init; }

    public string? Ref { get; init; }

    public int? Startline { get; init; }

    public long? ProjectId { get; init; }
}

public sealed record GitLabAwardEmoji
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;
}

public sealed record CreateMergeRequestRequest
{
    public required string SourceBranch { get; init; }

    public required string TargetBranch { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<long>? ReviewerIds { get; init; }

    public IReadOnlyList<string>? Labels { get; init; }

    public bool RemoveSourceBranch { get; init; } = true;
}

public sealed record UpdateMergeRequestRequest
{
    public string? Title { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<string>? Labels { get; init; }

    public IReadOnlyList<long>? ReviewerIds { get; init; }
}

public enum GitLabNoteableType
{
    Issue,
    MergeRequest,
}

public static class GitLabNoteableTypeExtensions
{
    /// <summary>The API path segment: <c>issues</c> or <c>merge_requests</c>.</summary>
    public static string ToPathSegment(this GitLabNoteableType type)
        => type == GitLabNoteableType.MergeRequest ? "merge_requests" : "issues";

    /// <summary>The to-do <c>target_type</c> value: <c>Issue</c> or <c>MergeRequest</c>.</summary>
    public static string ToTargetType(this GitLabNoteableType type)
        => type == GitLabNoteableType.MergeRequest ? "MergeRequest" : "Issue";

    public static bool TryParse(string? targetType, out GitLabNoteableType type)
    {
        if (string.Equals(targetType, "Issue", StringComparison.OrdinalIgnoreCase))
        {
            type = GitLabNoteableType.Issue;
            return true;
        }

        if (string.Equals(targetType, "MergeRequest", StringComparison.OrdinalIgnoreCase))
        {
            type = GitLabNoteableType.MergeRequest;
            return true;
        }

        type = default;
        return false;
    }
}
