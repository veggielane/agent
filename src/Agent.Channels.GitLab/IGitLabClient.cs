namespace Agent.Channels.GitLab;

/// <summary>
/// Thin typed client over the GitLab REST API v4. <c>projectId</c> parameters accept a numeric id or a
/// <c>group/project</c> path; both are URL-encoded by the client. Lookups return null on 404; every other
/// non-success status throws <see cref="GitLabApiException"/>.
/// </summary>
public interface IGitLabClient
{
    Task<GitLabUser> GetCurrentUserAsync(CancellationToken cancellationToken = default);

    Task<GitLabUser?> GetUserAsync(long id, CancellationToken cancellationToken = default);

    Task<GitLabUser?> GetUserByUsernameAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>Pending to-dos of the token's user, oldest first.</summary>
    Task<IReadOnlyList<GitLabTodo>> GetTodosAsync(CancellationToken cancellationToken = default);

    Task MarkTodoDoneAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Issues of a group (subgroups included) carrying all of <paramref name="labels"/>, oldest update first.</summary>
    Task<IReadOnlyList<GitLabIssue>> GetGroupIssuesAsync(string group, string labels, DateTimeOffset? updatedAfter, string state = "opened", CancellationToken cancellationToken = default);

    Task<GitLabProject?> GetProjectAsync(string idOrPath, CancellationToken cancellationToken = default);

    Task<GitLabIssue?> GetIssueAsync(string projectId, long iid, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitLabNote>> GetIssueNotesAsync(string projectId, long iid, CancellationToken cancellationToken = default);

    Task<GitLabMergeRequest?> GetMergeRequestAsync(string projectId, long iid, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitLabNote>> GetMergeRequestNotesAsync(string projectId, long iid, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitLabDiscussion>> GetMergeRequestDiscussionsAsync(string projectId, long iid, CancellationToken cancellationToken = default);

    Task<GitLabNote> CreateIssueNoteAsync(string projectId, long iid, string body, CancellationToken cancellationToken = default);

    Task<GitLabNote> CreateMergeRequestNoteAsync(string projectId, long iid, string body, CancellationToken cancellationToken = default);

    Task<GitLabNote> CreateDiscussionReplyAsync(string projectId, long mergeRequestIid, string discussionId, string body, CancellationToken cancellationToken = default);

    Task AwardEmojiOnNoteAsync(string projectId, GitLabNoteableType noteableType, long iid, long noteId, string emoji, CancellationToken cancellationToken = default);

    Task AwardEmojiAsync(string projectId, GitLabNoteableType noteableType, long iid, string emoji, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitLabMergeRequest>> ListMergeRequestsAsync(string projectId, string sourceBranch, string state = "opened", CancellationToken cancellationToken = default);

    Task<GitLabMergeRequest> CreateMergeRequestAsync(string projectId, CreateMergeRequestRequest request, CancellationToken cancellationToken = default);

    Task<GitLabMergeRequest> UpdateMergeRequestAsync(string projectId, long iid, UpdateMergeRequestRequest request, CancellationToken cancellationToken = default);

    /// <summary>Jobs of a pipeline, oldest stage first as GitLab returns them.</summary>
    Task<IReadOnlyList<GitLabJob>> GetPipelineJobsAsync(string projectId, long pipelineId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The last <paramref name="maxChars"/> characters of a job's log. Traces run to megabytes and the failure is at
    /// the end, so only the tail is kept; it is prefixed with an ellipsis when anything was dropped. Empty when the
    /// job has no trace (404).
    /// </summary>
    Task<string> GetJobTraceTailAsync(string projectId, long jobId, int maxChars, CancellationToken cancellationToken = default);

    /// <summary>Raw file content at <paramref name="reference"/> (branch, tag or SHA; project default when null). Null when missing.</summary>
    Task<string?> GetFileAsync(string projectId, string path, string? reference, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GitLabBlob>> SearchBlobsAsync(string projectId, string query, CancellationToken cancellationToken = default);
}
