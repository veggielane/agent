using System.Text.Json;
using Agent.Channels.Jira;
using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Tools;
using Microsoft.Extensions.AI;
using WireMock.RequestBuilders;
using WireMock.Server;

namespace Agent.Channels.Jira.Tests;

public sealed class JiraToolsTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => _server.Dispose();

    private JiraTools CreateTools(Action<JiraOptions>? configure = null)
    {
        var options = TestOptions.For(_server, configure);
        return new JiraTools(TestOptions.Client(_server, options), TestOptions.Monitor(options));
    }

    [Fact]
    public void GetTools_ExposesTwoReadOnlyToolsForUsers()
    {
        var tools = CreateTools().GetTools().ToList();

        Assert.Equal(["jira_get_issue", "jira_search"], tools.Select(t => t.Name).ToArray());
        Assert.All(tools, t =>
        {
            Assert.Equal(Role.Users, t.Role);
            Assert.Equal(ToolScope.All, t.Scope);
            Assert.Equal("native", t.Source);
            Assert.Null(t.Channels);
            Assert.False(string.IsNullOrWhiteSpace(t.Function.Description));
        });

        var schema = tools[1].Function.JsonSchema.GetRawText();
        Assert.Contains("jql", schema);
        Assert.Contains("max", schema);
        Assert.True(tools[0].AppliesTo(CallerIdentity.Local("u", Role.Users), ToolScope.Answer, Channel.Mattermost));
        Assert.True(tools[0].AppliesTo(CallerIdentity.Local("u", Role.Users), ToolScope.Coding, Channel.Cli));
    }

    [Fact]
    public async Task GetIssue_FormatsCompactTextWithLastFiveComments()
    {
        var comments = Enumerable.Range(1, 7).Select(i => Fx.Comment(i.ToString(), "bob", $"comment number {i}", $"2024-05-01T10:0{i}:00.000+0000")).ToArray();
        Stubs.Issue(_server, "PROJ-1", Fx.Issue("PROJ-1", "Login broken", description: "Users cannot log in.", labels: ["agent", "bug"], comments: comments, assignee: "carol", components: ["web"], status: "In Progress"));

        var text = await CreateTools().GetIssueAsync("proj-1", _ct);

        Assert.StartsWith("PROJ-1: Login broken", text);
        Assert.Contains("Status: In Progress", text);
        Assert.Contains("Assignee: carol", text);
        Assert.Contains("Reporter: alice", text);
        Assert.Contains("Labels: agent, bug", text);
        Assert.Contains("Components: web", text);
        Assert.Contains("Updated: 2024-05-01 10:19 UTC", text);
        Assert.Contains($"URL: {_server.Url}/browse/PROJ-1", text);
        Assert.Contains("Description:\nUsers cannot log in.", text);
        Assert.Contains("Comments (last 5 of 7):", text);
        Assert.DoesNotContain("comment number 1\n", text);
        Assert.DoesNotContain("comment number 2", text);
        Assert.Contains("[2024-05-01 10:03] bob: comment number 3", text);
        Assert.Contains("comment number 7", text);
        Assert.Equal("/rest/api/2/issue/PROJ-1", Assert.Single(_server.Requests()).Path);
    }

    [Fact]
    public async Task GetIssue_TruncatesLongDescriptions()
    {
        Stubs.Issue(_server, "PROJ-1", Fx.Issue("PROJ-1", "Long", description: new string('x', 5000)));

        var text = await CreateTools().GetIssueAsync("PROJ-1", _ct);

        Assert.Contains("[truncated 1000 characters]", text);
        Assert.True(text.Length < 4600);
    }

    [Fact]
    public async Task GetIssue_NotFound_ReturnsMessageNotError()
    {
        _server.Given(Request.Create().WithPath("/rest/api/2/issue/PROJ-9").UsingGet()).RespondWith(Stubs.Error(404, "nope"));

        var text = await CreateTools().GetIssueAsync("PROJ-9", _ct);

        Assert.Equal("Issue PROJ-9 was not found or is not visible to the agent.", text);
    }

    [Fact]
    public async Task GetIssue_ServerError_ReturnsErrorStringInsteadOfThrowing()
    {
        _server.Given(Request.Create().WithPath("/rest/api/2/issue/PROJ-1").UsingGet()).RespondWith(Stubs.Error(500, "boom"));

        var text = await CreateTools().GetIssueAsync("PROJ-1", _ct);

        Assert.StartsWith("Error: could not read PROJ-1", text);
        Assert.Contains("boom", text);
    }

    [Fact]
    public async Task GetIssue_BlankKey_ReturnsError()
    {
        Assert.StartsWith("Error:", await CreateTools().GetIssueAsync("  ", _ct));
        Assert.Empty(_server.LogEntries);
    }

    [Fact]
    public async Task Search_ListsKeyStatusSummaryAndClampsMax()
    {
        Stubs.Search(_server, Fx.SearchResult(3, 0, Fx.Issue("PROJ-1", "First"), Fx.Issue("PROJ-2", "Second\nline", status: "Done")));

        var text = await CreateTools().SearchAsync("project = PROJ", max: 500, _ct);

        Assert.Equal("Showing 2 of 3 issues:\nPROJ-1 | Open | First\nPROJ-2 | Done | Second line", text);
        var body = Assert.Single(_server.Requests()).JsonBody();
        Assert.Equal(50, body.GetProperty("maxResults").GetInt32());
        Assert.Equal("project = PROJ", body.GetProperty("jql").GetString());
        Assert.Equal(["summary", "status"], body.GetProperty("fields").EnumerateArray().Select(f => f.GetString()).ToArray());
    }

    [Fact]
    public async Task Search_NoResults_SaysSo()
    {
        Stubs.Search(_server, Fx.SearchResult());

        Assert.Equal("No issues match.", await CreateTools().SearchAsync("project = EMPTY", 10, _ct));
    }

    [Fact]
    public async Task Search_BadJql_ReturnsErrorString()
    {
        _server.Given(Request.Create().WithPath("/rest/api/2/search").UsingPost()).RespondWith(Stubs.Error(400, "Field 'foo' does not exist."));

        var text = await CreateTools().SearchAsync("foo = 1", 10, _ct);

        Assert.StartsWith("Error: search failed", text);
        Assert.Contains("Field 'foo' does not exist.", text);
    }

    [Fact]
    public async Task Tools_AreInvokableThroughTheAIFunction()
    {
        Stubs.Issue(_server, "PROJ-1", Fx.Issue("PROJ-1", "Via function"));
        Stubs.Search(_server, Fx.SearchResult(Fx.Issue("PROJ-2", "Found")));
        var tools = CreateTools().GetTools().ToDictionary(t => t.Name, t => t.Function);

        var issue = await tools["jira_get_issue"].InvokeAsync(new AIFunctionArguments { ["key"] = "PROJ-1" }, _ct);
        var search = await tools["jira_search"].InvokeAsync(new AIFunctionArguments { ["jql"] = "project = PROJ" }, _ct);

        Assert.Contains("PROJ-1: Via function", AsText(issue));
        Assert.Contains("PROJ-2 | Open | Found", AsText(search));
        var searchBody = _server.Requests().Single(r => r.Path == "/rest/api/2/search").JsonBody();
        Assert.Equal(10, searchBody.GetProperty("maxResults").GetInt32());
    }

    private static string AsText(object? result) => result switch
    {
        null => string.Empty,
        JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString()!,
        JsonElement element => element.GetRawText(),
        _ => result.ToString()!,
    };
}
