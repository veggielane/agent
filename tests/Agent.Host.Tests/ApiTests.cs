using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agent.Core.Tasks;
using Agent.Host.Tests.Support;

namespace Agent.Host.Tests;

public sealed class ApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly AgentApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Health_IsAnonymous()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/healthz", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Api_WithoutToken_IsUnauthorized()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/chat", new { text = "hi" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Api_WithWrongAudience_IsUnauthorized()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", AgentApiFactory.CreateToken("alice", ["/agent/users"], audience: "other"));
        var response = await client.PostAsJsonAsync("/api/chat", new { text = "hi" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Chat_AnswersAndKeepsConversationHistory()
    {
        _factory.Llm.Client.Reply("first answer").Reply("second answer");
        var client = _factory.CreateClientFor("alice", "/agent/users");

        var first = await Post<ChatDto>(client, "/api/chat", new { text = "hello", conversationId = "c1" });
        Assert.Equal("first answer", first.Markdown);
        Assert.Equal("c1", first.ConversationId);
        Assert.False(first.IsCommand);

        var second = await Post<ChatDto>(client, "/api/chat", new { text = "and then?", conversationId = "c1" });
        Assert.Equal("second answer", second.Markdown);

        var messages = _factory.Llm.Client.Calls[1];
        Assert.Equal(4, messages.Count);
        Assert.Equal("hello", messages[1].Text);
        Assert.Equal("first answer", messages[2].Text);
        Assert.Contains("alice", messages[0].Text);
    }

    [Fact]
    public async Task Chat_ClientModelOverride_IsIgnoredByDefault()
    {
        var client = _factory.CreateClientFor("alice", "/agent/users");
        await Post<ChatDto>(client, "/api/chat", new { text = "hello", model = "huge" });
        Assert.Null(_factory.Llm.Requests.Single().ExplicitModel);
    }

    [Fact]
    public async Task Chat_RunsCommands_WithRoles()
    {
        var client = _factory.CreateClientFor("bob", "/agent/team");

        var whoami = await Post<ChatDto>(client, "/api/chat", new { text = "!whoami" });
        Assert.True(whoami.IsCommand);
        Assert.Contains("bob", whoami.Markdown);
        Assert.Contains("Team", whoami.Markdown);

        var reload = await Post<ChatDto>(client, "/api/chat", new { text = "!reload" });
        Assert.True(reload.IsError);

        var help = await Post<ChatDto>(client, "/api/chat", new { text = "!help" });
        Assert.Contains("!tasks", help.Markdown);
        Assert.DoesNotContain("!reload", help.Markdown);
        Assert.Empty(_factory.Llm.Client.Calls);
    }

    [Fact]
    public async Task Chat_WithoutAnyRole_IsForbidden()
    {
        var client = _factory.CreateClientFor("nobody");
        var response = await client.PostAsJsonAsync("/api/chat", new { text = "hi" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Chat_Streams_ServerSentEvents()
    {
        _factory.Llm.Client.Reply("streamed words here");
        var client = _factory.CreateClientFor("alice", "/agent/users");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = JsonContent.Create(new { text = "hi", stream = true, conversationId = "s1" }) };
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("data: \"streamed \"", body);
        Assert.Contains("event: done", body);

        // History was recorded from the streamed reply.
        _factory.Llm.Client.Reply("next");
        await Post<ChatDto>(client, "/api/chat", new { text = "more", conversationId = "s1" });
        Assert.Equal("streamed words here", _factory.Llm.Client.Calls[1][2].Text.Trim());
    }

    [Fact]
    public async Task Tasks_Create_RepositoryRefusedByPolicy_Is403WithTheReason()
    {
        _factory.RepositoryPolicy = new DenyingPolicy();
        var team = _factory.CreateClientFor("bob", "/agent/team");

        var response = await team.PostAsJsonAsync("/api/tasks", new { repoUrl = "https://gitlab.test/other/repo.git", instruction = "do it" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, TestContext.Current.CancellationToken);
        Assert.Equal("`other/repo` is not among the projects I may work on.", body.GetProperty("error").GetString());
        var mine = await team.GetFromJsonAsync<List<TaskDto>>("/api/tasks", Json, TestContext.Current.CancellationToken);
        Assert.Empty(mine!);
    }

    private sealed class DenyingPolicy : IRepositoryPolicy
    {
        public RepositoryDecision Check(string repoUrl) => RepositoryDecision.Deny("`other/repo` is not among the projects I may work on.");
    }

    [Fact]
    public async Task Tasks_Lifecycle_WithRoles()
    {
        var team = _factory.CreateClientFor("bob", "/agent/team");
        var users = _factory.CreateClientFor("alice", "/agent/users");

        var forbidden = await users.PostAsJsonAsync("/api/tasks", new { repoUrl = "https://gitlab.test/t/r.git", instruction = "do it" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var created = await team.PostAsJsonAsync("/api/tasks", new { repoUrl = "https://gitlab.test/t/r.git", instruction = "add retries\nmore detail" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var task = (await created.Content.ReadFromJsonAsync<TaskDto>(Json, TestContext.Current.CancellationToken))!;
        Assert.Equal("add retries", task.Title);
        Assert.Equal("Cli", task.Source);
        Assert.Equal("https://gitlab.test/t/r.git", task.RepoUrl);

        var mine = await team.GetFromJsonAsync<List<TaskDto>>("/api/tasks", Json, TestContext.Current.CancellationToken);
        Assert.Single(mine!);

        var others = await users.GetFromJsonAsync<List<TaskDto>>("/api/tasks", Json, TestContext.Current.CancellationToken);
        Assert.Empty(others!);

        var allForbidden = await users.GetAsync("/api/tasks?all=true", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, allForbidden.StatusCode);

        var detail = await team.GetFromJsonAsync<TaskDto>($"/api/tasks/{task.Id}", Json, TestContext.Current.CancellationToken);
        Assert.Equal(task.Id, detail!.Id);

        var events = await team.GetFromJsonAsync<List<TaskEventDto>>($"/api/tasks/{task.Id}/events", Json, TestContext.Current.CancellationToken);
        Assert.Contains(events!, e => e.Type == "created");

        var otherCancel = await users.PostAsync($"/api/tasks/{task.Id}/cancel", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, otherCancel.StatusCode);

        var cancel = await team.PostAsync($"/api/tasks/{task.Id}/cancel", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var cancelled = await cancel.Content.ReadFromJsonAsync<TaskDto>(Json, TestContext.Current.CancellationToken);
        Assert.Equal("Cancelled", cancelled!.Status);

        var missing = await team.GetAsync("/api/tasks/999", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task WhoAmI_ReturnsRolesFromGroups()
    {
        var client = _factory.CreateClientFor("root", "/agent/admin", "role:dev");
        var me = await client.GetFromJsonAsync<JsonElement>("/api/whoami", Json, TestContext.Current.CancellationToken);

        Assert.Equal("root", me.GetProperty("username").GetString());
        var roles = me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Contains("Admin", roles);
        Assert.Contains("Users", roles);
        var groups = me.GetProperty("groups").EnumerateArray().Select(g => g.GetString()).ToList();
        Assert.Contains("/agent/admin", groups);
        Assert.Contains("role:dev", groups);
    }

    [Fact]
    public void ProjectPathFromUrl_StripsGitSuffix()
    {
        Assert.Equal("team/repo", Api.ApiEndpoints.ProjectPathFromUrl("https://gitlab.test/team/repo.git"));
        Assert.Equal("a/b/c", Api.ApiEndpoints.ProjectPathFromUrl("https://gitlab.test/a/b/c"));
        Assert.Null(Api.ApiEndpoints.ProjectPathFromUrl("not a url"));
    }

    private static async Task<T> Post<T>(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"{url} → {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        return (await response.Content.ReadFromJsonAsync<T>(Json, TestContext.Current.CancellationToken))!;
    }

    private sealed record ChatDto(string Markdown, string ConversationId, string? Model, List<string> ToolsUsed, bool IsCommand, bool IsError);

    private sealed record TaskDto(int Id, string Status, string Source, string SourceRef, string? Title, string? RepoUrl, string? WorkBranch, string? MergeRequestUrl, string? Summary, string? Error, int Turns, long TokensUsed, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string RequesterName);

    private sealed record TaskEventDto(DateTimeOffset At, string Type, string Message);
}
