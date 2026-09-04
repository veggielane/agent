using System.Net;
using System.Text.Json;
using Agent.Channels.Jira;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Agent.Channels.Jira.Tests;

public sealed class JiraClientTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task SearchAsync_PostsJqlFieldsAndBearerToken()
    {
        Stubs.Search(_server, Fx.SearchResult(Fx.Issue("PROJ-1", "First")));
        var client = TestOptions.Client(_server, TestOptions.For(_server));

        var result = await client.SearchAsync("project = \"PROJ\"", ["summary", "status"], 10, 25, _ct);

        Assert.Equal(1, result.Total);
        Assert.Equal("PROJ-1", Assert.Single(result.Issues).Key);
        var request = Assert.Single(_server.Requests());
        Assert.Equal("POST", request.Method);
        Assert.Equal("/rest/api/2/search", request.Path);
        Assert.Equal("Bearer secret-token", request.Header("Authorization"));
        Assert.Contains("application/json", request.Header("Content-Type"));
        var body = request.JsonBody();
        Assert.Equal("project = \"PROJ\"", body.GetProperty("jql").GetString());
        Assert.Equal(10, body.GetProperty("startAt").GetInt32());
        Assert.Equal(25, body.GetProperty("maxResults").GetInt32());
        Assert.Equal(["summary", "status"], body.GetProperty("fields").EnumerateArray().Select(f => f.GetString()).ToArray());
    }

    [Fact]
    public async Task SearchAsync_PasswordWithoutToken_UsesBasicAuth()
    {
        Stubs.Search(_server, Fx.SearchResult());
        var options = TestOptions.For(_server, o =>
        {
            o.Token = null;
            o.Password = "pa55";
        });

        await TestOptions.Client(_server, options).SearchAsync("project = X", null, 0, 10, _ct);

        var expected = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("agent-bot:pa55"));
        Assert.Equal(expected, Assert.Single(_server.Requests()).Header("Authorization"));
    }

    [Fact]
    public async Task GetIssueAsync_ParsesFieldsCustomFieldsAndJiraTimestamps()
    {
        var issue = Fx.Issue(
            "PROJ-7",
            "Broken build",
            description: "It fails.",
            labels: ["agent", "ci"],
            comments: [Fx.Comment("100", "bob", "[~agent-bot] why?", "2024-05-01T12:15:30.000+0200")],
            updated: "2024-05-01T12:19:00.000+0200",
            components: ["api"],
            extra: new Dictionary<string, object?> { ["customfield_12345"] = "https://gitlab.corp.local/team/repo" });
        Stubs.Issue(_server, "PROJ-7", issue);
        var client = TestOptions.Client(_server, TestOptions.For(_server));

        var result = await client.GetIssueAsync("PROJ-7", ["summary", "comment", "customfield_12345"], _ct);

        Assert.NotNull(result);
        Assert.Equal("PROJ-7", result.Key);
        Assert.Equal("Broken build", result.Fields.Summary);
        Assert.Equal(["agent", "ci"], result.Fields.Labels);
        Assert.Equal("Open", result.Fields.Status?.Name);
        Assert.Equal("PROJ", result.Fields.Project?.Key);
        Assert.Equal("api", Assert.Single(result.Fields.Components).Name);
        Assert.Equal("alice", result.Fields.Reporter?.Name);
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 10, 19, 0, TimeSpan.Zero), result.Fields.Updated);
        var comment = Assert.Single(result.Fields.Comment!.Comments);
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 10, 15, 30, TimeSpan.Zero), comment.Created);
        Assert.Equal("bob", comment.Author?.Name);
        Assert.Equal("https://gitlab.corp.local/team/repo", result.Fields.GetExtraString("customfield_12345"));

        var request = Assert.Single(_server.Requests());
        Assert.Equal("/rest/api/2/issue/PROJ-7", request.Path);
        Assert.Equal("summary,comment,customfield_12345", request.Query!["fields"].Single());
    }

    [Fact]
    public async Task GetIssueAsync_NotFound_ReturnsNull()
    {
        _server.Given(Request.Create().WithPath("/rest/api/2/issue/PROJ-404").UsingGet())
            .RespondWith(Stubs.Error(404, "Issue Does Not Exist"));

        var result = await TestOptions.Client(_server, TestOptions.For(_server)).GetIssueAsync("PROJ-404", null, _ct);

        Assert.Null(result);
    }

    [Fact]
    public async Task AddCommentAsync_PostsWikiBody()
    {
        Stubs.Comment(_server, "PROJ-1");

        var comment = await TestOptions.Client(_server, TestOptions.For(_server)).AddCommentAsync("PROJ-1", "h1. Hello\n\n*bold*", _ct);

        Assert.Equal("9001", comment.Id);
        var request = Assert.Single(_server.Requests());
        Assert.Equal("/rest/api/2/issue/PROJ-1/comment", request.Path);
        Assert.Equal("h1. Hello\n\n*bold*", request.JsonBody().GetProperty("body").GetString());
        Assert.Single(request.JsonBody().EnumerateObject());
    }

    [Fact]
    public async Task GetMyselfAsync_ReturnsUserWithTimeZone()
    {
        Stubs.Myself(_server, "Europe/Berlin");

        var me = await TestOptions.Client(_server, TestOptions.For(_server)).GetMyselfAsync(_ct);

        Assert.Equal("agent-bot", me.Name);
        Assert.Equal("Europe/Berlin", me.TimeZone);
    }

    [Fact]
    public async Task BaseUrlWithPathPrefix_IsPreserved()
    {
        _server.Given(Request.Create().WithPath("/jira/rest/api/2/myself").UsingGet()).RespondWith(Stubs.Json(new { name = "agent-bot" }));
        var options = TestOptions.For(_server, o => o.BaseUrl = _server.Url + "/jira/");

        var me = await TestOptions.Client(_server, options).GetMyselfAsync(_ct);

        Assert.Equal("agent-bot", me.Name);
    }

    [Fact]
    public async Task ErrorResponse_ThrowsJiraApiExceptionWithJiraMessage()
    {
        _server.Given(Request.Create().WithPath("/rest/api/2/search").UsingPost())
            .RespondWith(Stubs.Error(400, "Field 'projekt' does not exist."));

        var ex = await Assert.ThrowsAsync<JiraApiException>(() => TestOptions.Client(_server, TestOptions.For(_server)).SearchAsync("projekt = X", null, 0, 10, _ct));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Contains("Field 'projekt' does not exist.", ex.Message);
        Assert.Contains("400", ex.Message);
        Assert.False(ex.IsTransient);
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    public async Task ErrorResponse_MarksTransientStatuses(int status, bool transient)
    {
        _server.Given(Request.Create().WithPath("/rest/api/2/myself").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody("<html>oops</html>"));

        var ex = await Assert.ThrowsAsync<JiraApiException>(() => TestOptions.Client(_server, TestOptions.For(_server)).GetMyselfAsync(_ct));

        Assert.Equal(status, (int)ex.StatusCode);
        Assert.Equal(transient, ex.IsTransient);
        Assert.Contains("oops", ex.Message);
    }

    [Theory]
    [InlineData("2024-05-01T10:15:30.000+0000", "2024-05-01T10:15:30+00:00")]
    [InlineData("2024-05-01T12:15:30.000+0200", "2024-05-01T10:15:30+00:00")]
    [InlineData("2024-05-01T05:15:30.123-0500", "2024-05-01T10:15:30.123+00:00")]
    [InlineData("2024-05-01T10:15:30Z", "2024-05-01T10:15:30+00:00")]
    [InlineData("2024-05-01T10:15:30.000+00:00", "2024-05-01T10:15:30+00:00")]
    public void JiraTimestamp_ParsesCompactOffsets(string jira, string iso)
    {
        var parsed = JiraDateTimeOffsetConverter.Parse(jira);

        Assert.Equal(DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture), parsed);
    }

    [Fact]
    public void JiraTimestamp_NullableFieldsRoundTripThroughSerializer()
    {
        var json = """{"key":"P-1","fields":{"updated":"2024-05-01T10:15:30.000+0000","created":null}}""";

        var issue = JsonSerializer.Deserialize<JiraIssue>(json, JiraJson.Options)!;

        Assert.Equal(new DateTimeOffset(2024, 5, 1, 10, 15, 30, TimeSpan.Zero), issue.Fields.Updated);
        Assert.Null(issue.Fields.Created);
    }

    [Fact]
    public void ExtractErrorMessage_ReadsJiraErrorShapes()
    {
        Assert.Equal("a; b; field: bad", JiraClient.ExtractErrorMessage("""{"errorMessages":["a","b"],"errors":{"field":"bad"}}"""));
        Assert.Equal("plain text", JiraClient.ExtractErrorMessage("plain text"));
        Assert.Equal(string.Empty, JiraClient.ExtractErrorMessage(""));
    }
}
