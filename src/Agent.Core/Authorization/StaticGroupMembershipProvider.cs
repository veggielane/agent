namespace Agent.Core.Authorization;

/// <summary>
/// Group membership from configuration: <c>Authorization:StaticGroups:{username}</c> → groups.
/// For development and tests only.
/// </summary>
public sealed class StaticGroupMembershipProvider : IGroupMembershipProvider
{
    public const string ProviderName = "Static";

    private readonly Dictionary<string, string[]> _members;

    public StaticGroupMembershipProvider(IDictionary<string, string[]>? members = null)
    {
        _members = new Dictionary<string, string[]>(members ?? new Dictionary<string, string[]>(), StringComparer.OrdinalIgnoreCase);
    }

    public string Name => ProviderName;

    public Task<IReadOnlyCollection<string>> GetGroupsAsync(CallerIdentity identity, CancellationToken cancellationToken)
    {
        foreach (var key in new[] { identity.Username, identity.Email, identity.Upn, identity.ChannelUserId })
        {
            if (key is not null && _members.TryGetValue(key, out var groups))
            {
                return Task.FromResult<IReadOnlyCollection<string>>(groups);
            }
        }

        return Task.FromResult<IReadOnlyCollection<string>>([]);
    }
}
