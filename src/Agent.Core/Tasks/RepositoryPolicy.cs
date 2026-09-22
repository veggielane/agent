namespace Agent.Core.Tasks;

/// <summary>Whether the agent may clone and push to a repository, with the reason when it may not.</summary>
public sealed record RepositoryDecision(bool Allowed, string? Reason)
{
    public static readonly RepositoryDecision Allow = new(true, null);

    public static RepositoryDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// Decides which repositories coding tasks may target. Checked wherever a repository is attached to a task:
/// creation, a follow-up that names the repository, and again by the worker before it clones. Core allows
/// everything; the GitLab channel replaces this with its project allow list.
/// </summary>
public interface IRepositoryPolicy
{
    /// <summary><paramref name="reason"/> is written for the requester: it names the repository and what to do about it.</summary>
    RepositoryDecision Check(string repoUrl);
}

public sealed class AllowAllRepositoryPolicy : IRepositoryPolicy
{
    public RepositoryDecision Check(string repoUrl) => RepositoryDecision.Allow;
}

/// <summary>Thrown by <see cref="ITaskService.CreateAsync"/> when the policy refuses the repository. The message is the reason.</summary>
public sealed class RepositoryNotAllowedException : Exception
{
    public RepositoryNotAllowedException(string repoUrl, string reason)
        : base(reason)
    {
        RepoUrl = repoUrl;
    }

    public string RepoUrl { get; }
}
