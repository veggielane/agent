using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Infrastructure.Ldap;
using Agent.Infrastructure.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Agent.Infrastructure.Tests.Ldap;

public sealed class LdapGroupMembershipProviderTests
{
    private const string AliceDn = "CN=Alice (Admin),OU=Users,DC=corp,DC=local";

    private readonly FakeLdapSearcher _searcher = new();
    private readonly LdapOptions _options = new() { Enabled = true, Server = "dc01.corp.local", BaseDn = "DC=corp,DC=local" };

    [Fact]
    public async Task GetGroupsAsync_UserFoundByUpn_ReturnsGroupDnsAndCns()
    {
        _searcher.When(f => f.Contains("userPrincipalName=alice@corp.local", StringComparison.Ordinal), Entry(AliceDn));
        _searcher.When(
            f => f.Contains("objectClass=group", StringComparison.Ordinal),
            Entry("CN=agent-team,OU=Groups,DC=corp,DC=local", ("cn", ["agent-team"])),
            Entry("CN=agent-users,OU=Groups,DC=corp,DC=local", ("cn", ["agent-users"])));

        var provider = CreateProvider();
        var groups = await provider.GetGroupsAsync(new CallerIdentity(Channel.Mattermost, "mm-1", Upn: "alice@corp.local"), CancellationToken.None);

        Assert.Equal("Ldap", provider.Name);
        Assert.Equal(
            ["CN=agent-team,OU=Groups,DC=corp,DC=local", "agent-team", "CN=agent-users,OU=Groups,DC=corp,DC=local", "agent-users"],
            groups);

        Assert.Equal(2, _searcher.Searches.Count);
        var userSearch = _searcher.Searches[0];
        Assert.Equal("DC=corp,DC=local", userSearch.BaseDn);
        Assert.Equal("(|(sAMAccountName=alice@corp.local)(userPrincipalName=alice@corp.local)(mail=alice@corp.local))", userSearch.Filter);
        Assert.Equal(["distinguishedName"], userSearch.Attributes);

        var groupSearch = _searcher.Searches[1];
        Assert.Equal("(&(objectClass=group)(member:1.2.840.113556.1.4.1941:=CN=Alice \\28Admin\\29,OU=Users,DC=corp,DC=local))", groupSearch.Filter);
        Assert.Equal(["distinguishedName", "cn"], groupSearch.Attributes);
    }

    [Fact]
    public async Task GetGroupsAsync_TriesUsernameUpnEmailAndChannelIdInOrder()
    {
        _searcher.When(f => f.Contains("=42)", StringComparison.Ordinal), Entry("CN=Bob,DC=corp,DC=local"));
        _searcher.When(f => f.Contains("objectClass=group", StringComparison.Ordinal), Entry("CN=g,DC=corp,DC=local", ("cn", ["g"])));

        var groups = await CreateProvider().GetGroupsAsync(
            new CallerIdentity(Channel.GitLab, "42", Username: "bob", Email: "bob@corp.local", Upn: "bob@corp.local"),
            CancellationToken.None);

        Assert.Equal(["CN=g,DC=corp,DC=local", "g"], groups);
        Assert.Equal(4, _searcher.Searches.Count);
        Assert.Contains("(sAMAccountName=bob)", _searcher.Searches[0].Filter);
        Assert.Contains("(sAMAccountName=bob@corp.local)", _searcher.Searches[1].Filter);
        Assert.Contains("(sAMAccountName=42)", _searcher.Searches[2].Filter);
        Assert.Contains("objectClass=group", _searcher.Searches[3].Filter);
    }

    [Fact]
    public async Task GetGroupsAsync_EscapesFilterSpecialCharacters()
    {
        await CreateProvider().GetGroupsAsync(new CallerIdentity(Channel.Cli, "x", Username: "a*b(c)\\d"), CancellationToken.None);

        // Username first, then the channel user id as the last candidate.
        Assert.Equal(2, _searcher.Searches.Count);
        Assert.Equal("(|(sAMAccountName=a\\2ab\\28c\\29\\5cd)(userPrincipalName=a\\2ab\\28c\\29\\5cd)(mail=a\\2ab\\28c\\29\\5cd))", _searcher.Searches[0].Filter);
        Assert.Equal("(|(sAMAccountName=x)(userPrincipalName=x)(mail=x))", _searcher.Searches[1].Filter);
    }

    [Fact]
    public async Task GetGroupsAsync_UserNotFound_ReturnsEmptyWithoutGroupSearch()
    {
        var groups = await CreateProvider().GetGroupsAsync(new CallerIdentity(Channel.Jira, "ghost", Username: "ghost"), CancellationToken.None);

        Assert.Empty(groups);
        Assert.Single(_searcher.Searches);
    }

    [Fact]
    public async Task GetGroupsAsync_KnownLdapDn_SkipsUserSearch()
    {
        _searcher.When(f => f.Contains("objectClass=group", StringComparison.Ordinal), Entry("CN=g,DC=corp,DC=local", ("cn", ["g"])));

        var groups = await CreateProvider().GetGroupsAsync(new CallerIdentity(Channel.Cli, "alice", LdapDn: AliceDn), CancellationToken.None);

        Assert.Equal(["CN=g,DC=corp,DC=local", "g"], groups);
        var search = Assert.Single(_searcher.Searches);
        Assert.Contains("CN=Alice \\28Admin\\29", search.Filter);
    }

    [Fact]
    public async Task GetGroupsAsync_Disabled_ReturnsEmptyAndWarns()
    {
        _options.Enabled = false;
        var logger = new ListLogger<LdapGroupMembershipProvider>();

        var groups = await CreateProvider(logger).GetGroupsAsync(new CallerIdentity(Channel.Cli, "alice", Username: "alice"), CancellationToken.None);

        Assert.Empty(groups);
        Assert.Empty(_searcher.Searches);
        Assert.Contains(logger.Lines, l => l.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a*b", "a\\2ab")]
    [InlineData("(x)", "\\28x\\29")]
    [InlineData("back\\slash", "back\\5cslash")]
    [InlineData("nul\0", "nul\\00")]
    [InlineData("", "")]
    public void Escape_EncodesRfc4515SpecialCharacters(string input, string expected)
        => Assert.Equal(expected, LdapFilter.Escape(input));

    private LdapGroupMembershipProvider CreateProvider(ListLogger<LdapGroupMembershipProvider>? logger = null)
        => new(_searcher, TestOptions.Monitor(_options), logger ?? new ListLogger<LdapGroupMembershipProvider>());

    private static LdapEntry Entry(string dn, params (string Name, string[] Values)[] attributes)
    {
        var values = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["distinguishedName"] = [dn] };
        foreach (var (name, attributeValues) in attributes)
        {
            values[name] = attributeValues;
        }

        return new LdapEntry(dn, values);
    }

    private sealed class FakeLdapSearcher : ILdapSearcher
    {
        private readonly List<(Func<string, bool> Match, LdapEntry[] Entries)> _rules = [];

        public List<(string BaseDn, string Filter, string[] Attributes)> Searches { get; } = [];

        public void When(Func<string, bool> filterMatches, params LdapEntry[] entries) => _rules.Add((filterMatches, entries));

        public Task<IReadOnlyList<LdapEntry>> SearchAsync(string baseDn, string filter, string[] attributes, CancellationToken cancellationToken)
        {
            Searches.Add((baseDn, filter, attributes));
            var rule = _rules.FirstOrDefault(r => r.Match(filter));
            return Task.FromResult<IReadOnlyList<LdapEntry>>(rule.Entries ?? []);
        }
    }
}
