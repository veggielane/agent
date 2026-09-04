using Agent.Cli.Auth;
using Agent.Cli.Backends;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Agent.Host.Tests;

public sealed class RemoteBackendTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Dispose();

    private RemoteBackend Backend()
    {
        var http = new HttpClient { BaseAddress = new Uri(_server.Url! + "/") };
        return new RemoteBackend(http, new FixedTokenProvider("tok-123"));
    }

    [Fact]
    public async Task Chat_SendsBearer_AndParsesResponse()
    {
        _server.Given(Request.Create().WithPath("/api/chat").UsingPost().WithHeader("Authorization", "Bearer tok-123"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("{\"markdown\":\"hi\",\"conversationId\":\"c1\",\"model\":\"m\",\"toolsUsed\":[\"jira_get_issue\"],\"isCommand\":false,\"isError\":false}"));

        var result = await Backend().ChatAsync("hello", "c1", null, TestContext.Current.CancellationToken);

        Assert.Equal("hi", result.Markdown);
        Assert.Equal(["jira_get_issue"], result.ToolsUsed);
        var body = _server.LogEntries.Single().RequestMessage.Body;
        Assert.Contains("\"text\":\"hello\"", body);
    }

    [Fact]
    public async Task Stream_ParsesServerSentEvents()
    {
        _server.Given(Request.Create().WithPath("/api/chat").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/event-stream")
                .WithBody("data: \"hel\"\n\ndata: \"lo\"\n\nevent: done\ndata: {}\n\n"));

        var chunks = new List<string>();
        await foreach (var chunk in Backend().StreamAsync("hi", "c1", null, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(["hel", "lo"], chunks);
    }

    [Fact]
    public async Task Stream_FallsBackToJson_ForCommands()
    {
        _server.Given(Request.Create().WithPath("/api/chat").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("{\"markdown\":\"pong\",\"conversationId\":\"c1\",\"toolsUsed\":[],\"isCommand\":true,\"isError\":false}"));

        var chunks = new List<string>();
        await foreach (var chunk in Backend().StreamAsync("!ping", "c1", null, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(["pong"], chunks);
    }

    [Fact]
    public async Task Errors_MapToFriendlyMessages()
    {
        _server.Given(Request.Create().WithPath("/api/tasks").UsingGet()).RespondWith(Response.Create().WithStatusCode(403));
        var ex = await Assert.ThrowsAsync<CliException>(() => Backend().ListTasksAsync(false, 10, TestContext.Current.CancellationToken));
        Assert.Contains("not authorised", ex.Message);

        _server.Reset();
        _server.Given(Request.Create().WithPath("/api/tasks").UsingGet()).RespondWith(Response.Create().WithStatusCode(401));
        ex = await Assert.ThrowsAsync<CliException>(() => Backend().ListTasksAsync(false, 10, TestContext.Current.CancellationToken));
        Assert.Contains("agent login", ex.Message);
    }

    [Fact]
    public async Task Tasks_RoundTrip()
    {
        const string task = "{\"id\":7,\"status\":\"Queued\",\"source\":\"Cli\",\"sourceRef\":\"cli-1\",\"title\":\"t\",\"repoUrl\":\"u\",\"workBranch\":null,\"mergeRequestUrl\":null,\"summary\":null,\"error\":null,\"turns\":0,\"tokensUsed\":0,\"createdAt\":\"2026-09-04T10:00:00Z\",\"updatedAt\":\"2026-09-04T10:00:00Z\",\"requesterName\":\"bob\"}";
        _server.Given(Request.Create().WithPath("/api/tasks").UsingPost()).RespondWith(Response.Create().WithStatusCode(201).WithHeader("Content-Type", "application/json").WithBody(task));
        _server.Given(Request.Create().WithPath("/api/tasks/7").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(task));
        _server.Given(Request.Create().WithPath("/api/tasks/8").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));
        _server.Given(Request.Create().WithPath("/api/tasks/7/events").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("[{\"at\":\"2026-09-04T10:00:00Z\",\"type\":\"created\",\"message\":\"m\"}]"));

        var backend = Backend();
        var created = await backend.CreateTaskAsync("u", "do", "t", null, TestContext.Current.CancellationToken);
        Assert.Equal(7, created.Id);
        Assert.NotNull(await backend.GetTaskAsync(7, TestContext.Current.CancellationToken));
        Assert.Null(await backend.GetTaskAsync(8, TestContext.Current.CancellationToken));
        Assert.Single(await backend.GetTaskEventsAsync(7, TestContext.Current.CancellationToken));
    }

    private sealed class FixedTokenProvider(string token) : IAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult(token);
    }
}
