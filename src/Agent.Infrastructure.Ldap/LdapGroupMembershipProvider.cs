using System.Globalization;
using Agent.Core.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Infrastructure.Ldap;

/// <summary>
/// Resolves a caller's Active Directory groups directly: locate the user with <see cref="LdapOptions.UserSearchFilter"/>,
/// then find every group that contains the user's DN transitively (<c>LDAP_MATCHING_RULE_IN_CHAIN</c>).
/// Returns group DNs and also their <c>cn</c> values so <c>Authorization.Roles</c> can use either.
/// </summary>
public sealed class LdapGroupMembershipProvider : IGroupMembershipProvider
{
    public const string ProviderName = "Ldap";

    private const string DistinguishedName = "distinguishedName";
    private const string CommonName = "cn";

    private readonly ILdapSearcher _searcher;
    private readonly IOptionsMonitor<LdapOptions> _options;
    private readonly ILogger<LdapGroupMembershipProvider> _logger;

    public LdapGroupMembershipProvider(ILdapSearcher searcher, IOptionsMonitor<LdapOptions> options, ILogger<LdapGroupMembershipProvider> logger)
    {
        _searcher = searcher;
        _options = options;
        _logger = logger;
    }

    public string Name => ProviderName;

    public async Task<IReadOnlyCollection<string>> GetGroupsAsync(CallerIdentity identity, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            _logger.LogWarning("LDAP group provider selected but {Section}:Enabled is false; {Caller} gets no groups", LdapOptions.SectionName, identity.Key);
            return [];
        }

        var userDn = identity.LdapDn ?? await FindUserDnAsync(identity, options, cancellationToken).ConfigureAwait(false);
        if (userDn is null)
        {
            _logger.LogDebug("LDAP: no user found for {Caller}", identity.Key);
            return [];
        }

        var filter = string.Create(CultureInfo.InvariantCulture, $"(&(objectClass=group)(member:1.2.840.113556.1.4.1941:={LdapFilter.Escape(userDn)}))");
        var groups = await _searcher.SearchAsync(options.BaseDn, filter, [DistinguishedName, CommonName], cancellationToken).ConfigureAwait(false);

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            Add(group.First(DistinguishedName) ?? group.Dn);
            foreach (var cn in group.All(CommonName))
            {
                Add(cn);
            }
        }

        _logger.LogDebug("LDAP: {Caller} → {UserDn} with {Count} group identifiers", identity.Key, userDn, result.Count);
        return result;

        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
            {
                result.Add(value);
            }
        }
    }

    private async Task<string?> FindUserDnAsync(CallerIdentity identity, LdapOptions options, CancellationToken cancellationToken)
    {
        var candidates = new[] { identity.Username, identity.Upn, identity.Email, identity.ChannelUserId }
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            var filter = string.Format(CultureInfo.InvariantCulture, options.UserSearchFilter, LdapFilter.Escape(candidate));
            var users = await _searcher.SearchAsync(options.BaseDn, filter, [DistinguishedName], cancellationToken).ConfigureAwait(false);
            var user = users.FirstOrDefault();
            if (user is not null)
            {
                return user.First(DistinguishedName) ?? user.Dn;
            }
        }

        return null;
    }
}
