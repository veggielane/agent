namespace Agent.Core.Authorization;

/// <summary>Turns a channel identity into one with roles, using the configured group provider and a cache.</summary>
public interface IRoleResolver
{
    Task<CallerIdentity> ResolveAsync(CallerIdentity identity, CancellationToken cancellationToken);

    /// <summary>Pure mapping of group identifiers to the hierarchical role set. Used for token claims too.</summary>
    IReadOnlySet<Role> RolesFromGroups(IEnumerable<string> groups);

    /// <summary>Drops cached lookups so the next request hits the provider again.</summary>
    void Invalidate(CallerIdentity identity);
}
