using Agent.Infrastructure.Keycloak;
using Agent.Infrastructure.Keycloak.DeviceFlow;
using Agent.Infrastructure.Tests.Support;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Agent.Infrastructure.Tests.Keycloak;

public sealed class KeycloakDeviceFlowClientTests : IDisposable
{
    private const string DiscoveryPath = "/realms/corp/.well-known/openid-configuration";
    private const string DevicePath = "/realms/corp/protocol/openid-connect/auth/device";
    private const string TokenPath = "/realms/corp/protocol/openid-connect/token";

    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly HttpClient _http = new();
    private readonly MutableTimeProvider _time = new();
    private readonly List<TimeSpan> _delays = [];
    private readonly KeycloakOptions _options;

    public KeycloakDeviceFlowClientTests()
    {
        _options = new KeycloakOptions { Authority = $"{_server.Url}/realms/corp", CliClientId = "agent-cli" };

        _server.Given(Request.Create().WithPath(DiscoveryPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                issuer = $"{_server.Url}/realms/corp",
                token_endpoint = $"{_server.Url}{TokenPath}",
                device_authorization_endpoint = $"{_server.Url}{DevicePath}",
                authorization_endpoint = $"{_server.Url}/realms/corp/protocol/openid-connect/auth",
                end_session_endpoint = $"{_server.Url}/realms/corp/protocol/openid-connect/logout",
            }));
    }

    [Fact]
    public async Task DiscoverAsync_ReadsEndpointsAndCachesTheDocument()
    {
        var client = CreateClient();

        var first = await client.DiscoverAsync();
        var second = await client.DiscoverAsync();

        Assert.Equal($"{_server.Url}/realms/corp", first.Issuer);
        Assert.Equal($"{_server.Url}{TokenPath}", first.TokenEndpoint);
        Assert.Equal($"{_server.Url}{DevicePath}", first.DeviceAuthorizationEndpoint);
        Assert.Equal($"{_server.Url}/realms/corp/protocol/openid-connect/logout", first.EndSessionEndpoint);
        Assert.Same(first, second);
        Assert.Equal(1, _server.Requests().Count(e => e.Path == DiscoveryPath));
    }

    [Fact]
    public async Task StartAsync_PostsClientIdAndScope_ReturnsCodes()
    {
        _server.Given(Request.Create().WithPath(DevicePath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                device_code = "dev-code",
                user_code = "ABCD-EFGH",
                verification_uri = $"{_server.Url}/realms/corp/device",
                verification_uri_complete = $"{_server.Url}/realms/corp/device?user_code=ABCD-EFGH",
                expires_in = 600,
                interval = 5,
            }));

        var auth = await CreateClient().StartAsync();

        Assert.Equal("dev-code", auth.DeviceCode);
        Assert.Equal("ABCD-EFGH", auth.UserCode);
        Assert.Equal($"{_server.Url}/realms/corp/device", auth.VerificationUri);
        Assert.Equal($"{_server.Url}/realms/corp/device?user_code=ABCD-EFGH", auth.VerificationUriComplete);
        Assert.Equal(_time.Now.AddSeconds(600), auth.ExpiresAt);
        Assert.Equal(TimeSpan.FromSeconds(5), auth.Interval);

        var request = Assert.Single(_server.Requests(), e => e.Path == DevicePath);
        Assert.Contains("client_id=agent-cli", request.Body);
        Assert.Contains("scope=openid+profile+email", request.Body);
        Assert.Equal("application/x-www-form-urlencoded", request.Headers!["Content-Type"].Single().Split(';')[0]);
    }

    [Fact]
    public async Task PollAsync_PendingThenSlowDownThenSuccess_ReturnsTokensWithoutSleeping()
    {
        _server.Given(Request.Create().WithPath(TokenPath).UsingPost().WithBody(new WildcardMatcher("*device_code=dev-code*")))
            .InScenario("poll").WillSetStateTo("slow")
            .RespondWith(Response.Create().WithStatusCode(400).WithBodyAsJson(new { error = "authorization_pending", error_description = "waiting" }));
        _server.Given(Request.Create().WithPath(TokenPath).UsingPost().WithBody(new WildcardMatcher("*device_code=dev-code*")))
            .InScenario("poll").WhenStateIs("slow").WillSetStateTo("done")
            .RespondWith(Response.Create().WithStatusCode(400).WithBodyAsJson(new { error = "slow_down" }));
        _server.Given(Request.Create().WithPath(TokenPath).UsingPost().WithBody(new WildcardMatcher("*device_code=dev-code*")))
            .InScenario("poll").WhenStateIs("done")
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
            {
                access_token = "access",
                refresh_token = "refresh",
                id_token = "id",
                expires_in = 300,
                token_type = "Bearer",
            }));

        var auth = new DeviceAuthorization("dev-code", "ABCD", "https://sso/device", null, _time.Now.AddMinutes(10), TimeSpan.FromSeconds(5));

        var tokens = await CreateClient().PollAsync(auth);

        Assert.Equal("access", tokens.AccessToken);
        Assert.Equal("refresh", tokens.RefreshToken);
        Assert.Equal("id", tokens.IdToken);
        Assert.Equal(_time.Now.AddSeconds(300), tokens.ExpiresAt);
        Assert.False(tokens.IsExpired(_time));
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)], _delays);

        var polls = _server.Requests().Where(e => e.Path == TokenPath).ToList();
        Assert.Equal(3, polls.Count);
        Assert.All(polls, p => Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", p.Body));
        Assert.All(polls, p => Assert.Contains("client_id=agent-cli", p.Body));
    }

    [Fact]
    public async Task PollAsync_AccessDenied_ThrowsDeviceFlowException()
    {
        _server.Given(Request.Create().WithPath(TokenPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithBodyAsJson(new { error = "access_denied", error_description = "The user declined." }));

        var auth = new DeviceAuthorization("dev-code", "ABCD", "https://sso/device", null, _time.Now.AddMinutes(10), TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<DeviceFlowException>(() => CreateClient().PollAsync(auth));

        Assert.Equal("access_denied", ex.Error);
        Assert.Equal("The user declined.", ex.Description);
    }

    [Fact]
    public async Task PollAsync_CodeAlreadyExpired_ThrowsWithoutCallingTheServer()
    {
        var auth = new DeviceAuthorization("dev-code", "ABCD", "https://sso/device", null, _time.Now.AddSeconds(-1), TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<DeviceFlowException>(() => CreateClient().PollAsync(auth));

        Assert.Equal("expired_token", ex.Error);
        Assert.DoesNotContain(_server.Requests(), e => e.Path == TokenPath);
    }

    [Fact]
    public async Task RefreshAsync_PostsRefreshGrant_ReturnsNewTokens()
    {
        _server.Given(Request.Create().WithPath(TokenPath).UsingPost().WithBody(new WildcardMatcher("*grant_type=refresh_token*")))
            .RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { access_token = "access-2", refresh_token = "refresh-2", expires_in = 120 }));

        var tokens = await CreateClient().RefreshAsync("refresh-1");

        Assert.Equal("access-2", tokens.AccessToken);
        Assert.Equal("refresh-2", tokens.RefreshToken);
        Assert.Null(tokens.IdToken);
        Assert.Equal(_time.Now.AddSeconds(120), tokens.ExpiresAt);

        var request = Assert.Single(_server.Requests(), e => e.Path == TokenPath);
        Assert.Contains("refresh_token=refresh-1", request.Body);
        Assert.Contains("client_id=agent-cli", request.Body);
    }

    [Fact]
    public async Task RefreshAsync_InvalidGrant_ThrowsDeviceFlowException()
    {
        _server.Given(Request.Create().WithPath(TokenPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithBodyAsJson(new { error = "invalid_grant", error_description = "Token is not active" }));

        var ex = await Assert.ThrowsAsync<DeviceFlowException>(() => CreateClient().RefreshAsync("stale"));

        Assert.Equal("invalid_grant", ex.Error);
    }

    private KeycloakDeviceFlowClient CreateClient()
        => new(_http, TestOptions.Monitor(_options), _time, (delay, _) =>
        {
            _delays.Add(delay);
            return Task.CompletedTask;
        });

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
        _http.Dispose();
    }
}
