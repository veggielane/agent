using Agent.Core.Channels;

namespace Agent.Core.Authorization;

/// <summary>
/// Who is talking to the agent. Channel adapters fill in what they know; the role resolver adds
/// <see cref="Roles"/> and <see cref="Groups"/>.
/// </summary>
public sealed record CallerIdentity(
    Channel Channel,
    string ChannelUserId,
    string? Username = null,
    string? Email = null,
    string? Upn = null,
    string? LdapDn = null)
{
    public IReadOnlySet<Role> Roles { get; init; } = new HashSet<Role>();

    public IReadOnlyCollection<string> Groups { get; init; } = [];

    /// <summary>True once the role resolver has run for this identity.</summary>
    public bool RolesResolved { get; init; }

    public bool HasRole(Role role) => Roles.Contains(role);

    public string DisplayName => Username ?? Email ?? Upn ?? ChannelUserId;

    /// <summary>A stable key for caching and audit, independent of the channel's display data.</summary>
    public string Key => $"{Channel}:{ChannelUserId}";

    public CallerIdentity WithRoles(IReadOnlySet<Role> roles, IReadOnlyCollection<string> groups)
        => this with { Roles = roles, Groups = groups, RolesResolved = true };

    /// <summary>The identity used by the CLI in local mode and by tests.</summary>
    public static CallerIdentity Local(string username, params Role[] roles)
        => new CallerIdentity(Channel.Cli, username, username)
        {
            Roles = roles.Expand(),
            RolesResolved = roles.Length > 0,
        };
}
