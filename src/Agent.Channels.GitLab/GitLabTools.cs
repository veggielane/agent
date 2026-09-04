using System.ComponentModel;
using System.Globalization;
using System.Text;
using Agent.Core.Authorization;
using Agent.Core.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agent.Channels.GitLab;

/// <summary>Read-only GitLab tools for the answer loop. Failures come back as text so the model can react.</summary>
public sealed class GitLabTools : IToolSource
{
    internal const int MaxFileChars = 20_000;
    private const int RecentNotes = 5;
    private const int MaxBlobs = 10;
    private const int MaxDescriptionChars = 6_000;
    private const int MaxNoteChars = 1_500;
    private const int MaxSnippetChars = 800;

    private readonly IGitLabClient _client;
    private readonly ILogger<GitLabTools> _logger;

    public GitLabTools(IGitLabClient client, ILogger<GitLabTools> logger)
    {
        _client = client;
        _logger = logger;
    }

    public IEnumerable<ToolDescriptor> GetTools()
    {
        yield return Native(AIFunctionFactory.Create(GetIssueAsync, "gitlab_get_issue", "Reads a GitLab issue: title, state, labels, author, description and the last few comments."));
        yield return Native(AIFunctionFactory.Create(GetMergeRequestAsync, "gitlab_get_mr", "Reads a GitLab merge request: title, state, branches, labels, description and the last few comments."));
        yield return Native(AIFunctionFactory.Create(SearchCodeAsync, "gitlab_search_code", "Searches file contents of a GitLab project and returns matching files with a snippet."));
        yield return Native(AIFunctionFactory.Create(ReadFileAsync, "gitlab_read_file", "Reads a file from a GitLab project repository at a branch, tag or commit (default branch when omitted)."));
    }

    public async Task<string> GetIssueAsync(
        [Description("Project path (group/project) or numeric project id")] string project,
        [Description("Issue number (iid) within the project")] int iid,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var issue = await _client.GetIssueAsync(project, iid, cancellationToken).ConfigureAwait(false);
            if (issue is null)
            {
                return $"Error: issue {project}#{iid} was not found.";
            }

            var sb = new StringBuilder();
            sb.Append("Issue ").Append(issue.References?.Full ?? GitLabRefs.Issue(project, iid)).Append(": ").AppendLine(issue.Title);
            sb.Append("State: ").AppendLine(issue.State);
            sb.Append("Author: ").AppendLine(issue.Author?.Username ?? "unknown");
            sb.Append("Labels: ").AppendLine(issue.Labels is { Count: > 0 } ? string.Join(", ", issue.Labels) : "none");
            sb.Append("Updated: ").AppendLine(issue.UpdatedAt.ToString("u", CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(issue.WebUrl))
            {
                sb.Append("URL: ").AppendLine(issue.WebUrl);
            }

            sb.AppendLine().AppendLine("Description:").AppendLine(Truncate(issue.Description, MaxDescriptionChars, "(no description)"));
            await AppendRecentNotesAsync(sb, () => _client.GetIssueNotesAsync(project, iid, cancellationToken)).ConfigureAwait(false);
            return sb.ToString().TrimEnd();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "gitlab_get_issue {Project}#{Iid} failed", project, iid);
            return $"Error: {ex.Message}";
        }
    }

    public async Task<string> GetMergeRequestAsync(
        [Description("Project path (group/project) or numeric project id")] string project,
        [Description("Merge request number (iid) within the project")] int iid,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var mr = await _client.GetMergeRequestAsync(project, iid, cancellationToken).ConfigureAwait(false);
            if (mr is null)
            {
                return $"Error: merge request {project}!{iid} was not found.";
            }

            var sb = new StringBuilder();
            sb.Append("Merge request ").Append(GitLabRefs.MergeRequest(project, iid)).Append(": ").AppendLine(mr.Title);
            sb.Append("State: ").Append(mr.State);
            if (mr.Draft)
            {
                sb.Append(" (draft)");
            }

            sb.AppendLine();
            sb.Append("Branches: ").Append(mr.SourceBranch).Append(" -> ").AppendLine(mr.TargetBranch);
            sb.Append("Author: ").AppendLine(mr.Author?.Username ?? "unknown");
            sb.Append("Labels: ").AppendLine(mr.Labels is { Count: > 0 } ? string.Join(", ", mr.Labels) : "none");
            if (mr.MergedAt is not null)
            {
                sb.Append("Merged: ").AppendLine(mr.MergedAt.Value.ToString("u", CultureInfo.InvariantCulture));
            }

            if (!string.IsNullOrEmpty(mr.WebUrl))
            {
                sb.Append("URL: ").AppendLine(mr.WebUrl);
            }

            sb.AppendLine().AppendLine("Description:").AppendLine(Truncate(mr.Description, MaxDescriptionChars, "(no description)"));
            await AppendRecentNotesAsync(sb, () => _client.GetMergeRequestNotesAsync(project, iid, cancellationToken)).ConfigureAwait(false);
            return sb.ToString().TrimEnd();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "gitlab_get_mr {Project}!{Iid} failed", project, iid);
            return $"Error: {ex.Message}";
        }
    }

    public async Task<string> SearchCodeAsync(
        [Description("Project path (group/project) or numeric project id")] string project,
        [Description("Search text; GitLab matches file contents (and supports filename:, path:, extension: filters)")] string query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Error: query must not be empty.";
        }

        try
        {
            var blobs = await _client.SearchBlobsAsync(project, query, cancellationToken).ConfigureAwait(false);
            if (blobs.Count == 0)
            {
                return $"No files in {project} match \"{query}\".";
            }

            var sb = new StringBuilder();
            sb.Append(blobs.Count.ToString(CultureInfo.InvariantCulture)).Append(" match(es) in ").Append(project).Append(blobs.Count > MaxBlobs ? $"; showing {MaxBlobs.ToString(CultureInfo.InvariantCulture)}" : string.Empty).AppendLine(":");
            foreach (var blob in blobs.Take(MaxBlobs))
            {
                sb.AppendLine();
                sb.Append("## ").Append(blob.Path ?? blob.Filename ?? blob.Basename ?? "(unknown path)");
                if (blob.Startline is not null)
                {
                    sb.Append(" (line ").Append(blob.Startline.Value.ToString(CultureInfo.InvariantCulture)).Append(')');
                }

                if (!string.IsNullOrEmpty(blob.Ref))
                {
                    sb.Append(" @ ").Append(blob.Ref);
                }

                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(Truncate(blob.Data?.TrimEnd(), MaxSnippetChars, string.Empty));
                sb.AppendLine("```");
            }

            return sb.ToString().TrimEnd();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "gitlab_search_code in {Project} failed", project);
            return $"Error: {ex.Message}";
        }
    }

    public async Task<string> ReadFileAsync(
        [Description("Project path (group/project) or numeric project id")] string project,
        [Description("File path inside the repository, e.g. src/Program.cs")] string path,
        [Description("Branch, tag or commit SHA; the project's default branch when omitted")] string? @ref = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Error: path must not be empty.";
        }

        try
        {
            var reference = @ref;
            if (string.IsNullOrWhiteSpace(reference))
            {
                var projectInfo = await _client.GetProjectAsync(project, cancellationToken).ConfigureAwait(false);
                if (projectInfo is null)
                {
                    return $"Error: project {project} was not found.";
                }

                reference = projectInfo.DefaultBranch;
            }

            var content = await _client.GetFileAsync(project, path.Trim().TrimStart('/'), reference, cancellationToken).ConfigureAwait(false);
            if (content is null)
            {
                return $"Error: file {path} was not found in {project} at {reference ?? "the default branch"}.";
            }

            if (content.Length > MaxFileChars)
            {
                return content[..MaxFileChars] + $"\n\n[truncated: {content.Length.ToString(CultureInfo.InvariantCulture)} characters total, showing the first {MaxFileChars.ToString(CultureInfo.InvariantCulture)}]";
            }

            return content;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "gitlab_read_file {Project}:{Path} failed", project, path);
            return $"Error: {ex.Message}";
        }
    }

    private static ToolDescriptor Native(AIFunction function) => new(function, Role.Users, ToolScope.All, "native");

    private static async Task AppendRecentNotesAsync(StringBuilder sb, Func<Task<IReadOnlyList<GitLabNote>>> load)
    {
        IReadOnlyList<GitLabNote> notes;
        try
        {
            notes = await load().ConfigureAwait(false);
        }
        catch (GitLabApiException)
        {
            return;
        }

        var recent = notes.Where(n => !n.System && !string.IsNullOrWhiteSpace(n.Body)).OrderBy(n => n.CreatedAt).TakeLast(RecentNotes).ToList();
        if (recent.Count == 0)
        {
            return;
        }

        sb.AppendLine().Append("Last ").Append(recent.Count.ToString(CultureInfo.InvariantCulture)).AppendLine(" comment(s):");
        foreach (var note in recent)
        {
            sb.Append("- [").Append(note.Author?.Username ?? "unknown").Append(", ").Append(note.CreatedAt.ToString("u", CultureInfo.InvariantCulture)).Append("] ");
            sb.AppendLine(Truncate(note.Body, MaxNoteChars, string.Empty).ReplaceLineEndings("\n  "));
        }
    }

    private static string Truncate(string? text, int max, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return text.Length <= max ? text : text[..max] + " …[truncated]";
    }
}
