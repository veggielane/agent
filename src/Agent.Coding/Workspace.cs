using System.Security.Cryptography;
using Agent.Core.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Coding;

/// <summary>A checked-out repository for one task.</summary>
public sealed record Workspace(int TaskId, string Root, string RepoPath, string Branch, string BaseBranch, RepoProfile Profile);

public interface IWorkspaceManager
{
    /// <summary>Clones the task's repository, checks out or creates the work branch, and loads the repo profile.</summary>
    Task<Workspace> PrepareAsync(AgentTask task, (string Username, string Token)? credentials, CancellationToken cancellationToken);

    /// <summary>Deletes the workspace unless <paramref name="keep"/> is set (failed tasks are kept for inspection).</summary>
    Task CleanupAsync(Workspace workspace, bool keep, CancellationToken cancellationToken);

    /// <summary>Removes workspaces older than <see cref="CodingOptions.KeepFailedWorkspacesDays"/>.</summary>
    Task PurgeOldAsync(CancellationToken cancellationToken);
}

public sealed class WorkspaceManager : IWorkspaceManager
{
    private static readonly string[] AllowedSchemes = ["https", "http", "ssh", "file"];

    private readonly IGitRunner _git;
    private readonly IOptionsMonitor<CodingOptions> _options;
    private readonly ILogger<WorkspaceManager> _logger;

    public WorkspaceManager(IGitRunner git, IOptionsMonitor<CodingOptions> options, ILogger<WorkspaceManager> logger)
    {
        _git = git;
        _options = options;
        _logger = logger;
    }

    /// <summary><c>{prefix}{slug(sourceRef)}-{slug(title)}</c>, e.g. <c>agent/group-repo-12-add-login</c>.</summary>
    public static string BranchName(string prefix, string sourceRef, string? title)
    {
        var refSlug = TextUtil.Slug(sourceRef, 40);
        var titleSlug = TextUtil.Slug(title, 40);
        var name = string.IsNullOrEmpty(titleSlug) ? refSlug : $"{refSlug}-{titleSlug}";
        if (string.IsNullOrEmpty(name))
        {
            name = "task";
        }

        return prefix + name;
    }

    public async Task<Workspace> PrepareAsync(AgentTask task, (string Username, string Token)? credentials, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var repoUrl = task.RepoUrl?.Trim();
        if (string.IsNullOrWhiteSpace(repoUrl))
        {
            throw new InvalidOperationException($"Task {task.DisplayRef} has no repository URL.");
        }

        ValidateRepoUrl(repoUrl);

        var workspaceRoot = Path.GetFullPath(options.EffectiveWorkspaceRoot);
        Directory.CreateDirectory(workspaceRoot);
        RemoveStale(workspaceRoot, task.Id);

        var root = Path.Combine(workspaceRoot, $"{task.Id}-{RandomSuffix()}");
        var repoPath = Path.Combine(root, "repo");
        Directory.CreateDirectory(root);

        _logger.LogInformation("Cloning {Repo} for task {Task} into {Path}", Redact(repoUrl), task.DisplayRef, repoPath);
        await _git.CloneAsync(repoUrl, repoPath, credentials, options.CloneDepth, options.UseBlobFilter, cancellationToken).ConfigureAwait(false);

        var defaultBranch = await _git.CurrentBranchAsync(repoPath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(defaultBranch) || defaultBranch == "HEAD")
        {
            defaultBranch = "main";
        }

        var baseBranch = string.IsNullOrWhiteSpace(task.BaseBranch) ? defaultBranch : task.BaseBranch.Trim();
        string branch;

        if (!string.IsNullOrWhiteSpace(task.WorkBranch)
            && await _git.BranchExistsOnRemoteAsync(repoPath, task.WorkBranch, credentials, cancellationToken).ConfigureAwait(false))
        {
            branch = task.WorkBranch.Trim();
            await _git.FetchAsync(repoPath, TrackingRefspec(branch), credentials, options.CloneDepth, cancellationToken).ConfigureAwait(false);
            await _git.CheckoutAsync(repoPath, branch, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Task {Task} continues on existing branch {Branch}", task.DisplayRef, branch);
        }
        else
        {
            branch = string.IsNullOrWhiteSpace(task.WorkBranch)
                ? BranchName(options.BranchPrefix, task.SourceRef, task.Title)
                : task.WorkBranch.Trim();

            if (string.IsNullOrWhiteSpace(task.WorkBranch)
                && await _git.BranchExistsOnRemoteAsync(repoPath, branch, credentials, cancellationToken).ConfigureAwait(false))
            {
                branch = $"{branch}-{task.Id}";
            }

            if (!string.Equals(baseBranch, defaultBranch, StringComparison.Ordinal))
            {
                await _git.FetchAsync(repoPath, TrackingRefspec(baseBranch), credentials, options.CloneDepth, cancellationToken).ConfigureAwait(false);
            }

            await _git.CreateBranchAsync(repoPath, branch, $"origin/{baseBranch}", cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Task {Task} works on new branch {Branch} from {Base}", task.DisplayRef, branch, baseBranch);
        }

        var profile = RepoConfigLoader.Load(repoPath);
        return new Workspace(task.Id, root, repoPath, branch, baseBranch, profile);
    }

    public Task CleanupAsync(Workspace workspace, bool keep, CancellationToken cancellationToken)
    {
        if (keep)
        {
            _logger.LogInformation("Keeping workspace {Path} for task #{Task}", workspace.Root, workspace.TaskId);
            return Task.CompletedTask;
        }

        DeleteDirectory(workspace.Root);
        return Task.CompletedTask;
    }

    public Task PurgeOldAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var root = Path.GetFullPath(options.EffectiveWorkspaceRoot);
        if (!Directory.Exists(root))
        {
            return Task.CompletedTask;
        }

        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(0, options.KeepFailedWorkspacesDays));
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new DirectoryInfo(dir);
                var newest = info.CreationTimeUtc > info.LastWriteTimeUtc ? info.CreationTimeUtc : info.LastWriteTimeUtc;
                if (newest < cutoff)
                {
                    _logger.LogInformation("Purging old workspace {Path}", dir);
                    DeleteDirectory(dir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not purge {Path}", dir);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Deletes a directory tree, clearing read-only attributes that git sets on object files.</summary>
    public static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 2)
                {
                    throw;
                }

                Thread.Sleep(100 * (attempt + 1));
            }
        }
    }

    private static string TrackingRefspec(string branch) => $"+refs/heads/{branch}:refs/remotes/origin/{branch}";

    private static void ValidateRepoUrl(string url)
    {
        if (url.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Repository URL '{url}' is not an http(s), ssh, or file URL.");
        }
    }

    private void RemoveStale(string workspaceRoot, int taskId)
    {
        foreach (var dir in Directory.EnumerateDirectories(workspaceRoot, $"{taskId}-*"))
        {
            try
            {
                DeleteDirectory(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remove stale workspace {Path}", dir);
            }
        }
    }

    private static string RandomSuffix()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    private static string Redact(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo))
        {
            return uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
        }

        return url;
    }
}
