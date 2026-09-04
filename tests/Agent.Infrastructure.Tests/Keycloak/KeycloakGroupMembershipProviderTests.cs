using System.Net;
using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Infrastructure.Keycloak;
using Agent.Infrastructure.Tests.Support;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Agent.Infrastructure.Tests.Keycloak;

public sealed class KeycloakGroupMembershipProviderTests : IDisposable
{
    private const string TokenPath = "/realms/corp/protocol/openid-connect/token";
    private const string UsersPath = "/admin/realms/corp/users";

    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly HttpClient _http = new();
    private readonly MutableTimeProvider _time = new();
    private readonly KeycloakOptions _options;

    public KeycloakGroupMembershipProviderTests()
    {
        _options = new KeycloakOptions
        {
            Authority = $"{_server.Url}/realms/corp",
            Admin = { ClientId = "agent-service", ClientSecret = "s3cret" },
        };

        _server.Given(Request.Create().WithPath(TokenPath).UsingPost().WithBody(new WildcardMatcher("*grant_type=client_credentials*")))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { access_token = "admin-token", expires_in = 300, token_type = "Bearer" }));
    }

    [Fact]
    public void Options_DeriveRealmAndAdminBaseFromAuthority()
    {
        Assert.Equal("corp", _options.Realm);
        Assert.Equal(new Uri($"{_server.Url}/admin/realms/corp/"), _options.AdminBaseUri);
        Assert.Equal($"{_server.Url}{TokenPath}", _options.TokenEndpoint);

        var prefixed = new KeycloakOptions { Authority = "https://sso.corp.local/auth/realms/corp/" };
        Assert.Equal("corp", prefixed.Realm);
        Assert.Equal(new Uri("https://sso.corp.local/auth/admin/realms/corp/"), prefixed.AdminBaseUri);

        var explicitAdmin = new KeycloakOptions { Authority = "https://sso/realms/corp", Admin = { BaseUrl = "https://admin.sso/admin/realms/corp" } };
        Assert.Equal(new Uri("https://admin.sso/admin/realms/corp/"), explicitAdmin.AdminBaseUri);

        Assert.Throws<InvalidOperationException>(() => new KeycloakOptions { Authority = "https://sso.corp.local" }.Realm);
    }

    [Fact]
    public async Task GetGroupsAsync_UserFoundByUsername_ReturnsGroupPathsNamesAndRealmRoles()
    {
        StubUser("alice", "u-1");
        StubGroups("u-1", new { id = "g1", name = "team", path = "/agent/team" }, new { id = "g2", name = "users", path = "/agent/users" });
        StubRealmRoles("u-1", "developer", "offline_access");

        var provider = CreateProvider();
        var groups = await provider.GetGroupsAsync(new CallerIdentity(Channel.Mattermost, "mm-1", Username: "alice", Email: "alice@corp.local"), CancellationToken.None);

        Assert.Equal(["/agent/team", "team", "/agent/users", "users", "role:developer", "role:offline_access"], groups);
        Assert.Equal("Keycloak", provider.Name);

        var tokenRequest = Assert.Single(_server.Requests(), e => e.Path == TokenPath);
        Assert.Contains("grant_type=client_credentials", tokenRequest.Body);
        Assert.Contains("client_id=agent-service", tokenRequest.Body);
        Assert.Contains("client_secret=s3cret", tokenRequest.Body);

        var adminRequests = _server.Requests().Where(e => e.Path.StartsWith(UsersPath, StringComparison.Ordinal)).ToList();
        Assert.Equal(3, adminRequests.Count);
        Assert.All(adminRequests, r => Assert.Equal("Bearer admin-token", r.Headers!["Authorization"].Single()));
        Assert.DoesNotContain(_server.Requests(), e => e.Query!.TryGetValue("email", out _));
    }

    [Fact]
    public async Task GetGroupsAsync_UsernameUnknown_FallsBackToEmail()
    {
        StubNoUser("username", "alice");
        StubUserByEmail("alice@corp.local", "u-9");
        StubGroups("u-9", new { id = "g1", name = "team", path = "/agent/team" });
        StubRealmRoles("u-9");

        var groups = await CreateProvider().GetGroupsAsync(new CallerIdentity(Channel.Jira, "alice", Username: "alice", Email: "alice@corp.local"), CancellationToken.None);

        Assert.Equal(["/agent/team", "team"], groups);
        Assert.Contains(_server.Requests(), e => e.Query!.TryGetValue("email", out var v) && v.Contains("alice@corp.local"));
    }

    [Fact]
    public async Task GetGroupsAsync_UserNotFound_ReturnsEmpty()
    {
        StubNoUser("username", "ghost");
        StubNoUser("email", "ghost@corp.local");

        var groups = await CreateProvider().GetGroupsAsync(new CallerIdentity(Channel.GitLab, "42", Username: "ghost", Email: "ghost@corp.local"), CancellationToken.None);

        Assert.Empty(groups);
        Assert.DoesNotContain(_server.Requests(), e => e.Path.Contains("/groups", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetGroupsAsync_IncludeRealmRolesOff_SkipsRoleMappings()
    {
        _options.IncludeRealmRoles = false;
        StubUser("alice", "u-1");
        StubGroups("u-1", new { id = "g1", name = "team", path = "/agent/team" });

        var groups = await CreateProvider().GetGroupsAsync(new CallerIdentity(Channel.Mattermost, "mm-1", Username: "alice"), CancellationToken.None);

        Assert.Equal(["/agent/team", "team"], groups);
        Assert.DoesNotContain(_server.Requests(), e => e.Path.Contains("role-mappings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetGroupsAsync_ServerError_Throws()
    {
        _server.Given(Request.Create().WithPath(UsersPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500).WithBody("boom"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => CreateProvider().GetGroupsAsync(new CallerIdentity(Channel.Mattermost, "mm-1", Username: "alice"), CancellationToken.None));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public async Task GetGroupsAsync_TokenRequestFails_Throws()
    {
        _server.Reset();
        _server.Given(Request.Create().WithPath(TokenPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401).WithBodyAsJson(new { error = "invalid_client" }));

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateProvider().GetGroupsAsync(new CallerIdentity(Channel.Mattermost, "mm-1", Username: "alice"), CancellationToken.None));
    }

    [Fact]
    public async Task GetGroupsAsync_ReusesTokenUntilExpiry()
    {
        StubUser("alice", "u-1");
        StubGroups("u-1");
        StubRealmRoles("u-1");
        var provider = CreateProvider();
        var identity = new CallerIdentity(Channel.Mattermost, "mm-1", Username: "alice");

        await provider.GetGroupsAsync(identity, CancellationToken.None);
        await provider.GetGroupsAsync(identity, CancellationToken.None);
        Assert.Equal(1, _server.Requests().Count(e => e.Path == TokenPath));

        // 300 s lifetime minus the 30 s safety margin: at 271 s the token is treated as expired.
        _time.Advance(TimeSpan.FromSeconds(271));
        await provider.GetGroupsAsync(identity, CancellationToken.None);
        Assert.Equal(2, _server.Requests().Count(e => e.Path == TokenPath));
    }

    [Fact]
    public async Task GetGroupsAsync_TokenRejected_RefreshesOnceAndRetries()
    {
        // Drop the constructor's token stub: this test scripts the token endpoint itself.
        _server.Reset();
        _server.Given(Request.Create().WithPath(UsersPath).UsingGet().WithHeader("Authorization", "Bearer admin-token"))
            .RespondWith(Response.Create().WithStatusCode(401));
        _server.Given(Request.Create().WithPath(UsersPath).UsingGet().WithHeader("Authorization", "Bearer fresh-token"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[] { new { id = "u-1", username = "alice" } }));
        StubGroups("u-1", new { id = "g1", name = "team", path = "/agent/team" });
        StubRealmRoles("u-1");
        var provider = CreateProvider();
        var identity = new CallerIdentity(Channel.Mattermost, "mm-1", Username: "alice");

        // First token is cached, then rejected by the API; the second token request returns a fresh one.
        _server.Given(Request.Create().WithPath(TokenPath).UsingPost())
            .InScenario("token").WillSetStateTo("second")
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { access_token = "admin-token", expires_in = 300 }));
        _server.Given(Request.Create().WithPath(TokenPath).UsingPost())
            .InScenario("token").WhenStateIs("second")
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { access_token = "fresh-token", expires_in = 300 }));

        var groups = await provider.GetGroupsAsync(identity, CancellationToken.None);

        Assert.Equal(["/agent/team", "team"], groups);
        Assert.Equal(2, _server.Requests().Count(e => e.Path == TokenPath));
    }

    private KeycloakGroupMembershipProvider CreateProvider()
        => new(_http, TestOptions.Monitor(_options), new ListLogger<KeycloakGroupMembershipProvider>(), _time);

    private void StubUser(string username, string id)
        => _server.Given(Request.Create().WithPath(UsersPath).UsingGet().WithParam("username", username).WithParam("exact", "true"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[] { new { id, username } }));

    private void StubUserByEmail(string email, string id)
        => _server.Given(Request.Create().WithPath(UsersPath).UsingGet().WithParam("email", email).WithParam("exact", "true"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new[] { new { id, email } }));

    private void StubNoUser(string param, string value)
        => _server.Given(Request.Create().WithPath(UsersPath).UsingGet().WithParam(param, value))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(Array.Empty<object>()));

    private void StubGroups(string userId, params object[] groups)
        => _server.Given(Request.Create().WithPath($"{UsersPath}/{userId}/groups").UsingGet().WithParam("briefRepresentation", "true"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(groups));

    private void StubRealmRoles(string userId, params string[] roles)
        => _server.Given(Request.Create().WithPath($"{UsersPath}/{userId}/role-mappings/realm").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(roles.Select(r => new { id = Guid.NewGuid().ToString(), name = r }).ToArray()));

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
        _http.Dispose();
    }
}
