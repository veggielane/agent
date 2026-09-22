using Agent.Core.Tasks;
using Microsoft.Extensions.Options;

namespace Agent.Channels.GitLab;

/// <summary>
/// Restricts coding tasks to repositories under <see cref="GitLabOptions.AllowedProjects"/> on the configured
/// GitLab host. With the list empty the bot token remains the only boundary, which is how it worked before the
/// list existed; once a path is listed, anything else, including other hosts, is refused with a reason the
/// requester can act on.
/// </summary>
public sealed class GitLabRepositoryPolicy : IRepositoryPolicy
{
    private readonly IOptionsMonitor<GitLabOptions> _options;

    public GitLabRepositoryPolicy(IOptionsMonitor<GitLabOptions> options)
    {
        _options = options;
    }

    public RepositoryDecision Check(string repoUrl)
    {
        var options = _options.CurrentValue;
        var allowed = options.AllowedProjects
            .Select(NormalizePath)
            .Where(p => p.Length > 0)
            .ToList();
        if (allowed.Count == 0)
        {
            return RepositoryDecision.Allow;
        }

        if (!Uri.TryCreate(repoUrl?.Trim(), UriKind.Absolute, out var repo))
        {
            return RepositoryDecision.Deny($"`{repoUrl}` is not a repository URL I can work with.");
        }

        if (!Uri.TryCreate(options.BaseUrl.Trim(), UriKind.Absolute, out var gitlab)
            || !string.Equals(repo.Host, gitlab.Host, StringComparison.OrdinalIgnoreCase))
        {
            return RepositoryDecision.Deny($"I only work on repositories hosted on `{gitlab?.Host ?? options.BaseUrl}`, not `{repo.Host}`.");
        }

        var path = ProjectPath(repo, gitlab);
        if (path is null)
        {
            return RepositoryDecision.Deny($"`{repo.AbsolutePath.Trim('/')}` is not under the GitLab instance at `{options.BaseUrl.Trim().TrimEnd('/')}`.");
        }

        if (allowed.Any(prefix => IsUnder(path, prefix)))
        {
            return RepositoryDecision.Allow;
        }

        return RepositoryDecision.Deny($"`{path}` is not among the projects I may work on. Ask an administrator to add it to `GitLab:AllowedProjects`.");
    }

    /// <summary>
    /// The project path of a clone URL: the base URL's own path segment, the leading slash and <c>.git</c> removed.
    /// Null when the GitLab instance lives under a path root (<c>https://host/gitlab</c>) and the URL is outside it.
    /// </summary>
    internal static string? ProjectPath(Uri repo, Uri gitlab)
    {
        var path = NormalizePath(repo.AbsolutePath);
        var root = NormalizePath(gitlab.AbsolutePath);
        if (root.Length > 0)
        {
            if (!IsUnder(path, root))
            {
                return null;
            }

            path = path.Length == root.Length ? string.Empty : path[(root.Length + 1)..];
        }

        return path.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="prefix"/> or lies inside it, segment by segment.</summary>
    internal static bool IsUnder(string path, string prefix)
        => path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string? value)
        => Uri.UnescapeDataString(value ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
}
