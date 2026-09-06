using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Agent.Channels.GitLab;

/// <summary>
/// Typed <see cref="HttpClient"/> for GitLab. The DI registration sets the base address and the PRIVATE-TOKEN
/// header; tests point the base address at WireMock.
/// </summary>
public sealed class GitLabClient : IGitLabClient
{
    private const int MaxPages = 20;

    private readonly HttpClient _http;

    public GitLabClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<GitLabUser> GetCurrentUserAsync(CancellationToken cancellationToken = default)
        => await GetAsync<GitLabUser>("api/v4/user", cancellationToken).ConfigureAwait(false)
           ?? throw new GitLabApiException(HttpStatusCode.NotFound, "current user not found", null);

    public Task<GitLabUser?> GetUserAsync(long id, CancellationToken cancellationToken = default)
        => GetAsync<GitLabUser>($"api/v4/users/{id}", cancellationToken);

    public async Task<GitLabUser?> GetUserByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        var users = await GetAsync<List<GitLabUser>>($"api/v4/users?username={Encode(username)}", cancellationToken).ConfigureAwait(false);
        return users?.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)) ?? users?.FirstOrDefault();
    }

    public Task<IReadOnlyList<GitLabTodo>> GetTodosAsync(CancellationToken cancellationToken = default)
        => GetAllPagesAsync<GitLabTodo>("api/v4/todos?state=pending&per_page=100", cancellationToken);

    public Task MarkTodoDoneAsync(long id, CancellationToken cancellationToken = default)
        => PostAsync($"api/v4/todos/{id}/mark_as_done", null, cancellationToken);

    public Task<IReadOnlyList<GitLabIssue>> GetGroupIssuesAsync(string group, string labels, DateTimeOffset? updatedAfter, string state = "opened", CancellationToken cancellationToken = default)
    {
        var url = $"api/v4/groups/{Encode(group)}/issues?labels={Encode(labels)}&state={Encode(state)}&order_by=updated_at&sort=asc&per_page=100";
        if (updatedAfter is not null)
        {
            url += $"&updated_after={Encode(FormatInstant(updatedAfter.Value))}";
        }

        return GetAllPagesAsync<GitLabIssue>(url, cancellationToken);
    }

    public Task<GitLabProject?> GetProjectAsync(string idOrPath, CancellationToken cancellationToken = default)
        => GetAsync<GitLabProject>($"api/v4/projects/{Encode(idOrPath)}", cancellationToken);

    public Task<GitLabIssue?> GetIssueAsync(string projectId, long iid, CancellationToken cancellationToken = default)
        => GetAsync<GitLabIssue>($"api/v4/projects/{Encode(projectId)}/issues/{iid}", cancellationToken);

    public Task<IReadOnlyList<GitLabNote>> GetIssueNotesAsync(string projectId, long iid, CancellationToken cancellationToken = default)
        => GetAllPagesAsync<GitLabNote>($"api/v4/projects/{Encode(projectId)}/issues/{iid}/notes?sort=asc&order_by=created_at&per_page=100", cancellationToken);

    public Task<GitLabMergeRequest?> GetMergeRequestAsync(string projectId, long iid, CancellationToken cancellationToken = default)
        => GetAsync<GitLabMergeRequest>($"api/v4/projects/{Encode(projectId)}/merge_requests/{iid}", cancellationToken);

    public Task<IReadOnlyList<GitLabNote>> GetMergeRequestNotesAsync(string projectId, long iid, CancellationToken cancellationToken = default)
        => GetAllPagesAsync<GitLabNote>($"api/v4/projects/{Encode(projectId)}/merge_requests/{iid}/notes?sort=asc&order_by=created_at&per_page=100", cancellationToken);

    public Task<IReadOnlyList<GitLabDiscussion>> GetMergeRequestDiscussionsAsync(string projectId, long iid, CancellationToken cancellationToken = default)
        => GetAllPagesAsync<GitLabDiscussion>($"api/v4/projects/{Encode(projectId)}/merge_requests/{iid}/discussions?per_page=100", cancellationToken);

    public async Task<GitLabNote> CreateIssueNoteAsync(string projectId, long iid, string body, CancellationToken cancellationToken = default)
        => await PostAsync<GitLabNote>($"api/v4/projects/{Encode(projectId)}/issues/{iid}/notes", new { body }, cancellationToken).ConfigureAwait(false);

    public async Task<GitLabNote> CreateMergeRequestNoteAsync(string projectId, long iid, string body, CancellationToken cancellationToken = default)
        => await PostAsync<GitLabNote>($"api/v4/projects/{Encode(projectId)}/merge_requests/{iid}/notes", new { body }, cancellationToken).ConfigureAwait(false);

    public async Task<GitLabNote> CreateDiscussionReplyAsync(string projectId, long mergeRequestIid, string discussionId, string body, CancellationToken cancellationToken = default)
        => await PostAsync<GitLabNote>($"api/v4/projects/{Encode(projectId)}/merge_requests/{mergeRequestIid}/discussions/{Encode(discussionId)}/notes", new { body }, cancellationToken).ConfigureAwait(false);

    public Task AwardEmojiOnNoteAsync(string projectId, GitLabNoteableType noteableType, long iid, long noteId, string emoji, CancellationToken cancellationToken = default)
        => PostAsync($"api/v4/projects/{Encode(projectId)}/{noteableType.ToPathSegment()}/{iid}/notes/{noteId}/award_emoji", new { name = emoji }, cancellationToken);

    public Task AwardEmojiAsync(string projectId, GitLabNoteableType noteableType, long iid, string emoji, CancellationToken cancellationToken = default)
        => PostAsync($"api/v4/projects/{Encode(projectId)}/{noteableType.ToPathSegment()}/{iid}/award_emoji", new { name = emoji }, cancellationToken);

    public Task<IReadOnlyList<GitLabMergeRequest>> ListMergeRequestsAsync(string projectId, string sourceBranch, string state = "opened", CancellationToken cancellationToken = default)
        => GetAllPagesAsync<GitLabMergeRequest>($"api/v4/projects/{Encode(projectId)}/merge_requests?source_branch={Encode(sourceBranch)}&state={Encode(state)}&per_page=100", cancellationToken);

    public async Task<GitLabMergeRequest> CreateMergeRequestAsync(string projectId, CreateMergeRequestRequest request, CancellationToken cancellationToken = default)
    {
        var payload = Payload(
            ("source_branch", request.SourceBranch),
            ("target_branch", request.TargetBranch),
            ("title", request.Title),
            ("description", request.Description),
            ("remove_source_branch", request.RemoveSourceBranch),
            ("reviewer_ids", request.ReviewerIds is { Count: > 0 } ? request.ReviewerIds : null),
            ("labels", request.Labels is { Count: > 0 } ? string.Join(",", request.Labels) : null));

        return await PostAsync<GitLabMergeRequest>($"api/v4/projects/{Encode(projectId)}/merge_requests", payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitLabMergeRequest> UpdateMergeRequestAsync(string projectId, long iid, UpdateMergeRequestRequest request, CancellationToken cancellationToken = default)
    {
        var payload = Payload(
            ("title", request.Title),
            ("description", request.Description),
            ("reviewer_ids", request.ReviewerIds is { Count: > 0 } ? request.ReviewerIds : null),
            ("labels", request.Labels is { Count: > 0 } ? string.Join(",", request.Labels) : null));

        using var response = await _http.PutAsJsonAsync($"api/v4/projects/{Encode(projectId)}/merge_requests/{iid}", payload, GitLabJson.Options, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<GitLabMergeRequest>(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<GitLabJob>> GetPipelineJobsAsync(string projectId, long pipelineId, CancellationToken cancellationToken = default)
        => GetAllPagesAsync<GitLabJob>($"api/v4/projects/{Encode(projectId)}/pipelines/{pipelineId}/jobs?per_page=100", cancellationToken);

    public async Task<string> GetJobTraceTailAsync(string projectId, long jobId, int maxChars, CancellationToken cancellationToken = default)
    {
        var limit = Math.Max(1, maxChars);
        using var response = await _http
            .GetAsync($"api/v4/projects/{Encode(projectId)}/jobs/{jobId}/trace", HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return string.Empty;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        // Streamed and trimmed as it arrives: a trace can be hundreds of megabytes, and only the tail is wanted.
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream);
            var buffer = new char[4096];
            var tail = new StringBuilder(Math.Min(limit, 8192));
            var dropped = false;
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                tail.Append(buffer, 0, read);
                if (tail.Length > limit)
                {
                    tail.Remove(0, tail.Length - limit);
                    dropped = true;
                }
            }

            return dropped ? "…" + tail.ToString() : tail.ToString();
        }
    }

    public async Task<string?> GetFileAsync(string projectId, string path, string? reference, CancellationToken cancellationToken = default)
    {
        var url = $"api/v4/projects/{Encode(projectId)}/repository/files/{Encode(path)}/raw";
        if (!string.IsNullOrWhiteSpace(reference))
        {
            url += $"?ref={Encode(reference)}";
        }

        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GitLabBlob>> SearchBlobsAsync(string projectId, string query, CancellationToken cancellationToken = default)
        => await GetAsync<List<GitLabBlob>>($"api/v4/projects/{Encode(projectId)}/search?scope=blobs&search={Encode(query)}&per_page=20", cancellationToken).ConfigureAwait(false) ?? [];

    /// <summary>Builds a JSON body without null members (a null <c>reviewer_ids</c> would clear the reviewers).</summary>
    private static Dictionary<string, object> Payload(params (string Key, object? Value)[] fields)
    {
        var payload = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in fields)
        {
            if (value is not null)
            {
                payload[key] = value;
            }
        }

        return payload;
    }

    /// <summary>URL-encodes an id, path or query value: <c>group/project</c> becomes <c>group%2Fproject</c>.</summary>
    internal static string Encode(string value) => Uri.EscapeDataString(value);

    internal static string FormatInstant(DateTimeOffset instant)
        => instant.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private async Task<T?> GetAsync<T>(string url, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(GitLabJson.Options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Follows GitLab's <c>X-Next-Page</c> header until it is empty (or <see cref="MaxPages"/> is hit).</summary>
    private async Task<IReadOnlyList<T>> GetAllPagesAsync<T>(string url, CancellationToken cancellationToken)
    {
        var results = new List<T>();
        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var page = 1;
        while (page > 0 && page <= MaxPages)
        {
            using var response = await _http.GetAsync($"{url}{separator}page={page}", cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            var items = await response.Content.ReadFromJsonAsync<List<T>>(GitLabJson.Options, cancellationToken).ConfigureAwait(false) ?? [];
            results.AddRange(items);
            if (items.Count == 0)
            {
                break;
            }

            page = response.Headers.TryGetValues("X-Next-Page", out var values)
                   && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var next)
                   && next > page
                ? next
                : 0;
        }

        return results;
    }

    private async Task<T> PostAsync<T>(string url, object? payload, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync(url, JsonContent.Create(payload ?? new { }, options: GitLabJson.Options), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task PostAsync(string url, object? payload, CancellationToken cancellationToken)
    {
        using var content = payload is null ? null : JsonContent.Create(payload, options: GitLabJson.Options);
        using var response = await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        => await response.Content.ReadFromJsonAsync<T>(GitLabJson.Options, cancellationToken).ConfigureAwait(false)
           ?? throw new GitLabApiException(response.StatusCode, "empty response body", response.RequestMessage?.RequestUri);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? body = null;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // the status code is enough
        }

        if (body is { Length: > 500 })
        {
            body = body[..500] + "…";
        }

        throw new GitLabApiException(response.StatusCode, body, response.RequestMessage?.RequestUri);
    }
}
