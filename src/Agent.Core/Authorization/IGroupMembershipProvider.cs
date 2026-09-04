namespace Agent.Core.Authorization;

/// <summary>
/// Looks up the groups a caller belongs to. Implementations: Keycloak Admin API, direct LDAP, static config.
/// Group identifiers are opaque strings compared case-insensitively against <see cref="AuthorizationOptions.Roles"/>.
/// </summary>
public interface IGroupMembershipProvider
{
    /// <summary>Matches <see cref="AuthorizationOptions.Provider"/>.</summary>
    string Name { get; }

    Task<IReadOnlyCollection<string>> GetGroupsAsync(CallerIdentity identity, CancellationToken cancellationToken);
}
