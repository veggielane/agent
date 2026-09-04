using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Core.Authorization;

public sealed class RoleResolver : IRoleResolver
{
    private readonly IEnumerable<IGroupMembershipProvider> _providers;
    private readonly IOptionsMonitor<AuthorizationOptions> _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<RoleResolver> _logger;

    public RoleResolver(
        IEnumerable<IGroupMembershipProvider> providers,
        IOptionsMonitor<AuthorizationOptions> options,
        IMemoryCache cache,
        ILogger<RoleResolver> logger)
    {
        _providers = providers;
        _options = options;
        _cache = cache;
        _logger = logger;
    }

    public async Task<CallerIdentity> ResolveAsync(CallerIdentity identity, CancellationToken cancellationToken)
    {
        if (identity.RolesResolved)
        {
            return identity;
        }

        var options = _options.CurrentValue;
        var cacheKey = CacheKey(identity);

        if (options.CacheMinutes > 0 && _cache.TryGetValue(cacheKey, out CachedGroups? cached) && cached is not null)
        {
            return identity.WithRoles(RolesFromGroups(cached.Groups), cached.Groups);
        }

        var provider = SelectProvider(options.Provider);
        IReadOnlyCollection<string> groups;
        try
        {
            groups = await provider.GetGroupsAsync(identity, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed: an unreachable directory never grants access.
            _logger.LogError(ex, "Group lookup failed for {Caller} via {Provider}; treating as no groups", identity.Key, provider.Name);
            groups = [];
        }

        if (options.CacheMinutes > 0)
        {
            _cache.Set(cacheKey, new CachedGroups(groups), TimeSpan.FromMinutes(options.CacheMinutes));
        }

        var roles = RolesFromGroups(groups);
        _logger.LogDebug("Resolved {Caller} to roles [{Roles}] from {GroupCount} groups", identity.Key, string.Join(",", roles), groups.Count);
        return identity.WithRoles(roles, groups);
    }

    public IReadOnlySet<Role> RolesFromGroups(IEnumerable<string> groups)
    {
        var options = _options.CurrentValue;
        var groupSet = new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);
        var granted = new List<Role>();

        foreach (var role in Enum.GetValues<Role>())
        {
            if (options.GroupsFor(role).Any(groupSet.Contains))
            {
                granted.Add(role);
            }
        }

        return granted.Expand();
    }

    public void Invalidate(CallerIdentity identity) => _cache.Remove(CacheKey(identity));

    private IGroupMembershipProvider SelectProvider(string name)
    {
        var provider = _providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (provider is not null)
        {
            return provider;
        }

        var fallback = _providers.FirstOrDefault()
            ?? throw new InvalidOperationException($"No IGroupMembershipProvider is registered (configured provider: '{name}').");

        _logger.LogWarning("Group provider '{Configured}' is not registered; using '{Fallback}'", name, fallback.Name);
        return fallback;
    }

    private static string CacheKey(CallerIdentity identity)
        => $"roles:{identity.Key}:{identity.Username}:{identity.Email}";

    private sealed record CachedGroups(IReadOnlyCollection<string> Groups);
}
