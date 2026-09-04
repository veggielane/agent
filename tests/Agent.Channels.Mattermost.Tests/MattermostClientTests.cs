using System.Net;
using System.Text.Json;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Agent.Channels.Mattermost.Tests;

public sealed class MattermostClientTests : IDisposable
{
    private const string Token = "secret-token";

    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly HttpClient _http = new();
    private readonly MattermostClient _client;

    public MattermostClientTests()
    {
        _client = new MattermostClient(_http, new StaticOptionsMonitor<MattermostOptions>(new MattermostOptions
        {
            BaseUrl = _server.Url!,
            BotToken = Token,
        }));
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
        _http.Dispose();
    }

    [Fact]
    public async Task GetMeAsync_SendsBearerToken_AndMapsUser()
    {
        Stub(Request.Create().WithPath("/api/v4/users/me").UsingGet().WithHeader("Authorization", "Bearer " + Token),
            """{"id":"bot1","username":"agent","email":"agent@corp.local","is_bot":true}""");

        var me = await _client.GetMeAsync(TestContext.Current.CancellationToken);

        Assert.Equal("bot1", me.Id);
        Assert.Equal("agent", me.Username);
        Assert.Equal("agent@corp.local", me.Email);
        Assert.True(me.IsBot);
        var request = SingleRequest();
        Assert.Equal("Bearer " + Token, Assert.Single(request.Headers!["Authorization"]));
    }

    [Fact]
    public async Task GetUserAsync_MapsSnakeCaseFields()
    {
        Stub(Request.Create().WithPath("/api/v4/users/u1").UsingGet(),
            """{"id":"u1","username":"alice","email":"alice@corp.local","is_bot":false,"first_name":"Alice"}""");

        var user = await _client.GetUserAsync("u1", TestContext.Current.CancellationToken);

        Assert.NotNull(user);
        Assert.Equal("alice", user.Username);
        Assert.False(user.IsBot);
    }

    [Fact]
    public async Task GetUserAsync_NotFound_ReturnsNull()
    {
        _server.Given(Request.Create().WithPath("/api/v4/users/missing").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404).WithBody("""{"id":"store.sql_user.get.app_error","message":"not found","status_code":404}"""));

        var user = await _client.GetUserAsync("missing", TestContext.Current.CancellationToken);

        Assert.Null(user);
    }

    [Fact]
    public async Task GetUserAsync_ServerError_ThrowsApiException()
    {
        _server.Given(Request.Create().WithPath("/api/v4/users/u1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(403).WithBody("""{"message":"forbidden"}"""));

        var ex = await Assert.ThrowsAsync<MattermostApiException>(() => _client.GetUserAsync("u1", TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Contains("forbidden", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetChannelAsync_MapsType()
    {
        Stub(Request.Create().WithPath("/api/v4/channels/c1").UsingGet(),
            """{"id":"c1","type":"P","name":"dev-team","display_name":"Dev Team"}""");

        var channel = await _client.GetChannelAsync("c1", TestContext.Current.CancellationToken);

        Assert.NotNull(channel);
        Assert.Equal("P", channel.Type);
        Assert.Equal("dev-team", channel.Name);
        Assert.Equal("Dev Team", channel.DisplayName);
    }

    [Fact]
    public async Task GetPostAsync_MapsSnakeCaseFields()
    {
        Stub(Request.Create().WithPath("/api/v4/posts/p1").UsingGet(),
            """{"id":"p1","create_at":1725400000000,"update_at":1725400000001,"user_id":"u1","channel_id":"c1","root_id":"r1","message":"hi","type":"","props":{}}""");

        var post = await _client.GetPostAsync("p1", TestContext.Current.CancellationToken);

        Assert.NotNull(post);
        Assert.Equal(1725400000000, post.CreateAt);
        Assert.Equal("u1", post.UserId);
        Assert.Equal("c1", post.ChannelId);
        Assert.Equal("r1", post.RootId);
        Assert.True(post.IsReply);
        Assert.False(post.IsSystemPost);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1725400000000), post.CreatedAt);
    }

    [Fact]
    public async Task GetThreadAsync_OrdersPostsByCreateAt()
    {
        Stub(Request.Create().WithPath("/api/v4/posts/r1/thread").UsingGet(),
            """
            {"order":["p3","r1","p2"],
             "posts":{
               "p3":{"id":"p3","create_at":3000,"user_id":"u1","channel_id":"c1","root_id":"r1","message":"third"},
               "r1":{"id":"r1","create_at":1000,"user_id":"u1","channel_id":"c1","root_id":"","message":"first"},
               "p2":{"id":"p2","create_at":2000,"user_id":"bot1","channel_id":"c1","root_id":"r1","message":"second"}}}
            """);

        var thread = await _client.GetThreadAsync("r1", TestContext.Current.CancellationToken);

        Assert.Equal(["r1", "p2", "p3"], thread.Select(p => p.Id).ToArray());
    }

    [Fact]
    public async Task GetPostsForChannelAsync_SendsSinceQuery()
    {
        Stub(Request.Create().WithPath("/api/v4/channels/c1/posts").WithParam("since", "1000").UsingGet(),
            """{"order":["p2"],"posts":{"p2":{"id":"p2","create_at":2000,"user_id":"u1","channel_id":"c1","message":"later"}}}""");

        var posts = await _client.GetPostsForChannelAsync("c1", 1000, null, TestContext.Current.CancellationToken);

        var post = Assert.Single(posts);
        Assert.Equal("p2", post.Id);
        var request = SingleRequest();
        Assert.Equal("1000", Assert.Single(request.Query!["since"]));
        Assert.False(request.Query.ContainsKey("per_page"));
    }

    [Fact]
    public async Task GetPostsForChannelAsync_WithoutSince_SendsPerPageOnly()
    {
        Stub(Request.Create().WithPath("/api/v4/channels/c1/posts").UsingGet(),
            """{"order":[],"posts":{}}""");

        var posts = await _client.GetPostsForChannelAsync("c1", null, 31, TestContext.Current.CancellationToken);

        Assert.Empty(posts);
        var request = SingleRequest();
        Assert.Equal("31", Assert.Single(request.Query!["per_page"]));
        Assert.False(request.Query.ContainsKey("since"));
    }

    [Fact]
    public async Task CreatePostAsync_PostsSnakeCaseBody_WithRoot()
    {
        Stub(Request.Create().WithPath("/api/v4/posts").UsingPost(),
            """{"id":"new1","create_at":5000,"user_id":"bot1","channel_id":"c1","root_id":"r1","message":"reply"}""");

        var created = await _client.CreatePostAsync("c1", "reply", "r1", TestContext.Current.CancellationToken);

        Assert.Equal("new1", created.Id);
        var body = RequestBody("/api/v4/posts");
        Assert.Equal("c1", body.GetProperty("channel_id").GetString());
        Assert.Equal("reply", body.GetProperty("message").GetString());
        Assert.Equal("r1", body.GetProperty("root_id").GetString());
    }

    [Fact]
    public async Task CreatePostAsync_WithoutRoot_OmitsRootId()
    {
        Stub(Request.Create().WithPath("/api/v4/posts").UsingPost(),
            """{"id":"new1","create_at":5000,"user_id":"bot1","channel_id":"c1","root_id":"","message":"reply"}""");

        await _client.CreatePostAsync("c1", "reply", null, TestContext.Current.CancellationToken);

        var body = RequestBody("/api/v4/posts");
        Assert.False(body.TryGetProperty("root_id", out _));
    }

    [Fact]
    public async Task UpdatePostAsync_PutsIdAndMessage()
    {
        Stub(Request.Create().WithPath("/api/v4/posts/p1").UsingPut(),
            """{"id":"p1","create_at":5000,"update_at":6000,"user_id":"bot1","channel_id":"c1","message":"edited"}""");

        var updated = await _client.UpdatePostAsync("p1", "edited", TestContext.Current.CancellationToken);

        Assert.Equal("edited", updated.Message);
        var body = RequestBody("/api/v4/posts/p1");
        Assert.Equal("p1", body.GetProperty("id").GetString());
        Assert.Equal("edited", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task AddReactionAsync_PostsReaction()
    {
        Stub(Request.Create().WithPath("/api/v4/reactions").UsingPost(),
            """{"user_id":"bot1","post_id":"p1","emoji_name":"eyes","create_at":7000}""");

        await _client.AddReactionAsync("bot1", "p1", "eyes", TestContext.Current.CancellationToken);

        var body = RequestBody("/api/v4/reactions");
        Assert.Equal("bot1", body.GetProperty("user_id").GetString());
        Assert.Equal("p1", body.GetProperty("post_id").GetString());
        Assert.Equal("eyes", body.GetProperty("emoji_name").GetString());
    }

    [Fact]
    public async Task RemoveReactionAsync_DeletesReaction()
    {
        Stub(Request.Create().WithPath("/api/v4/users/bot1/posts/p1/reactions/eyes").UsingDelete(),
            """{"status":"OK"}""");

        await _client.RemoveReactionAsync("bot1", "p1", "eyes", TestContext.Current.CancellationToken);

        var request = SingleRequest();
        Assert.Equal("DELETE", request.Method);
        Assert.Equal("/api/v4/users/bot1/posts/p1/reactions/eyes", request.Path);
    }

    [Fact]
    public async Task RemoveReactionAsync_Failure_ThrowsApiException()
    {
        _server.Given(Request.Create().WithPath("/api/v4/users/bot1/posts/p1/reactions/eyes").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(400).WithBody("""{"message":"no such reaction"}"""));

        var ex = await Assert.ThrowsAsync<MattermostApiException>(() => _client.RemoveReactionAsync("bot1", "p1", "eyes", TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }

    [Fact]
    public void NormalizeBaseUrl_AddsTrailingSlash()
    {
        Assert.Equal(new Uri("https://chat.example.test/"), MattermostClient.NormalizeBaseUrl("https://chat.example.test"));
        Assert.Equal(new Uri("https://chat.example.test/mm/"), MattermostClient.NormalizeBaseUrl(" https://chat.example.test/mm/ "));
    }

    private void Stub(IRequestBuilder request, string json)
        => _server.Given(request).RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(json));

    private IRequestMessage SingleRequest()
    {
        var entry = Assert.Single(_server.LogEntries);
        Assert.NotNull(entry);
        Assert.NotNull(entry.RequestMessage);
        return entry.RequestMessage;
    }

    private JsonElement RequestBody(string path)
    {
        var entry = Assert.Single(_server.LogEntries, e => e?.RequestMessage?.Path == path);
        Assert.NotNull(entry);
        Assert.NotNull(entry.RequestMessage);
        Assert.NotNull(entry.RequestMessage.Body);
        return JsonDocument.Parse(entry.RequestMessage.Body).RootElement.Clone();
    }
}
