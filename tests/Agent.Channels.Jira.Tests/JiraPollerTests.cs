using System.Text.Json;
using Agent.Channels.Jira;
using Agent.Core.Events;
using Agent.Core.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.Server;

namespace Agent.Channels.Jira.Tests;

public sealed class JiraPollerTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly InboundQueue _queue = new();
    private readonly InMemoryCursorStore _cursors = new();
    private readonly InMemoryProcessedEventStore _processed = new();
    private readonly ITaskStore _tasks = Substitute.For<ITaskStore>();
    private readonly FakeTimeProvider _time = new(Fx.Now);

    public JiraPollerTests()
    {
        _tasks.FindActiveBySourceAsync(Arg.Any<TaskSource>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((AgentTask?)null);
    }

    public void Dispose() => _server.Dispose();

    private JiraPoller CreatePoller(JiraOptions options)
        => new(TestOptions.Client(_server, options), _queue, _cursors, _processed, _tasks, TestOptions.Monitor(options), NullLogger<JiraPoller>.Instance, _time);

    private static object[] SampleIssues() =>
    [
        Fx.Issue("PROJ-1", "Login broken", comments: [Fx.Comment("501", "bob", "[~agent-bot] why does login fail?", Fx.T1017)], updated: Fx.T1017),
        Fx.Issue("PROJ-2", "Add export", description: "CSV please", labels: ["agent"], updated: Fx.T1019),
        Fx.Issue("PROJ-3", "Unrelated change", updated: Fx.T1018),
    ];

    [Fact]
    public async Task PollOnce_FirstRun_EnqueuesMentionAndLabelThenIsIdempotent()
    {
        Stubs.Search(_server, Fx.SearchResult(SampleIssues()));
        var options = TestOptions.For(_server, o => o.RepositoryField = "customfield_12345");
        var poller = CreatePoller(options);

        var delay = await poller.PollOnceAsync(_ct);

        Assert.Equal(TimeSpan.FromSeconds(1), delay);
        Assert.Equal(2, _queue.Depth);
        var events = await Queues.DrainAsync(_queue, 2, _ct);
        var message = Assert.Single(events, e => e.Kind == InboundKind.Message);
        Assert.Equal("comment:501", message.EventId);
        Assert.Equal("why does login fail?", message.Text);
        var task = Assert.Single(events, e => e.Kind == InboundKind.TaskRequest);
        Assert.Equal("label:PROJ-2:agent", task.EventId);
        Assert.Equal("Add export\n\nCSV please", task.Text);
        Assert.Equal("alice", task.Caller.ChannelUserId);

        // Watermark = max(updated) of the page, stored as an ISO instant.
        var watermark = await _cursors.GetAsync("jira:PROJ", _ct);
        Assert.Equal(Fx.At(Fx.T1019), DateTimeOffset.Parse(watermark!, System.Globalization.CultureInfo.InvariantCulture));

        // The JQL covers the first-run window: now - overlap - overlap, minute granularity, and asks for the custom field.
        var search = _server.Requests().Single(r => r.Path == "/rest/api/2/search").JsonBody();
        Assert.Equal("project = \"PROJ\" AND updated >= \"2024-05-01 10:16\" ORDER BY updated ASC", search.GetProperty("jql").GetString());
        var fields = search.GetProperty("fields").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("comment", fields);
        Assert.Contains("labels", fields);
        Assert.Contains("customfield_12345", fields);
        Assert.Equal(50, search.GetProperty("maxResults").GetInt32());

        Assert.Contains("3 issues seen", poller.Status);
        Assert.Contains("2 events enqueued", poller.Status);

        // Second poll with the same data: same window overlap, but nothing is enqueued again.
        _server.ResetLogEntries();
        _time.Now = Fx.Now.AddSeconds(30);
        await poller.PollOnceAsync(_ct);

        Assert.Equal(0, _queue.Depth);
        var second = _server.Requests().Single(r => r.Path == "/rest/api/2/search").JsonBody();
        Assert.Equal("project = \"PROJ\" AND updated >= \"2024-05-01 10:17\" ORDER BY updated ASC", second.GetProperty("jql").GetString());
        Assert.Contains("6 issues seen", poller.Status);
        Assert.Contains("2 events enqueued", poller.Status);
        Assert.DoesNotContain("last error", poller.Status);
    }

    [Fact]
    public async Task PollOnce_ServerError_KeepsWatermarkAndBacksOffExponentially()
    {
        await _cursors.SetAsync("jira:PROJ", "2024-05-01T10:00:00.0000000+00:00", _ct);
        _server.Given(Request.Create().WithPath("/rest/api/2/search").UsingPost()).RespondWith(Stubs.Error(500, "boom"));
        var poller = CreatePoller(TestOptions.For(_server, o => o.PollSeconds = 10));

        var first = await poller.PollOnceAsync(_ct);
        var second = await poller.PollOnceAsync(_ct);

        Assert.Equal(TimeSpan.FromSeconds(20), first);
        Assert.Equal(TimeSpan.FromSeconds(40), second);
        Assert.Equal("2024-05-01T10:00:00.0000000+00:00", await _cursors.GetAsync("jira:PROJ", _ct));
        Assert.Equal(0, _queue.Depth);
        Assert.Contains("last error: PROJ:", poller.Status);
        Assert.Contains("boom", poller.Status);

        _server.Reset();
        Stubs.Search(_server, Fx.SearchResult());
        var recovered = await poller.PollOnceAsync(_ct);

        Assert.Equal(TimeSpan.FromSeconds(10), recovered);
        Assert.Equal(TimeSpan.Zero, poller.CurrentBackoff);
        Assert.DoesNotContain("last error", poller.Status);
    }

    [Fact]
    public async Task PollOnce_BackoffIsCappedAtFiveMinutes()
    {
        _server.Given(Request.Create().WithPath("/rest/api/2/search").UsingPost()).RespondWith(Stubs.Error(429, "slow down"));
        var poller = CreatePoller(TestOptions.For(_server, o => o.PollSeconds = 100));

        TimeSpan delay = TimeSpan.Zero;
        for (var i = 0; i < 6; i++)
        {
            delay = await poller.PollOnceAsync(_ct);
        }

        Assert.Equal(TimeSpan.FromMinutes(5), delay);
        Assert.Null(await _cursors.GetAsync("jira:PROJ", _ct));
    }

    [Fact]
    public async Task PollOnce_NonTransientError_DoesNotBackOff()
    {
        _server.Given(Request.Create().WithPath("/rest/api/2/search").UsingPost()).RespondWith(Stubs.Error(400, "bad jql"));
        var poller = CreatePoller(TestOptions.For(_server, o => o.PollSeconds = 7));

        var delay = await poller.PollOnceAsync(_ct);

        Assert.Equal(TimeSpan.FromSeconds(7), delay);
        Assert.Contains("bad jql", poller.Status);
    }

    [Fact]
    public async Task PollOnce_PagesThroughResultsAndAdvancesWatermarkPerPage()
    {
        var pageOne = Fx.SearchResult(2, 0, Fx.Issue("PROJ-1", "One", comments: [Fx.Comment("1", "bob", "[~agent-bot] a", Fx.T1017)], updated: Fx.T1017));
        var pageTwo = Fx.SearchResult(2, 1, Fx.Issue("PROJ-2", "Two", comments: [Fx.Comment("2", "bob", "[~agent-bot] b", Fx.T1019)], updated: Fx.T1019));
        _server.Given(Request.Create().WithPath("/rest/api/2/search").UsingPost().WithBody(new JsonPartialMatcher(new { startAt = 0 }))).RespondWith(Stubs.Json(pageOne));
        _server.Given(Request.Create().WithPath("/rest/api/2/search").UsingPost().WithBody(new JsonPartialMatcher(new { startAt = 1 }))).RespondWith(Stubs.Json(pageTwo));
        var poller = CreatePoller(TestOptions.For(_server, o => o.MaxResultsPerPoll = 1));

        await poller.PollOnceAsync(_ct);

        var events = await Queues.DrainAsync(_queue, 2, _ct);
        Assert.Equal(["comment:1", "comment:2"], events.Select(e => e.EventId).ToArray());
        Assert.Equal(2, _server.Requests().Count(r => r.Path == "/rest/api/2/search"));
        Assert.Equal(Fx.At(Fx.T1019), JiraPoller.ParseWatermark(await _cursors.GetAsync("jira:PROJ", _ct)));
    }

    [Fact]
    public async Task PollOnce_ActiveTask_TurnsMentionIntoFollowUpAndSkipsLabel()
    {
        var active = new AgentTask { Id = 7, Source = TaskSource.JiraIssue, SourceRef = "PROJ-1", Status = AgentTaskStatus.AwaitingReview, WorkBranch = "agent/PROJ-1" };
        _tasks.FindActiveBySourceAsync(TaskSource.JiraIssue, "PROJ-1", Arg.Any<CancellationToken>()).Returns(active);
        Stubs.Search(_server, Fx.SearchResult(
            Fx.Issue("PROJ-1", "Login broken", labels: ["agent"], comments: [Fx.Comment("501", "bob", "[~agent-bot] also fix logout", Fx.T1017)], updated: Fx.T1017)));
        var poller = CreatePoller(TestOptions.For(_server));

        await poller.PollOnceAsync(_ct);

        var evt = Assert.Single(await Queues.DrainAsync(_queue, 1, _ct));
        Assert.Equal(InboundKind.FollowUp, evt.Kind);
        Assert.Equal(7, evt.Task?.ExistingTaskId);
        Assert.Equal("agent/PROJ-1", evt.Task?.ExistingBranch);
        Assert.Equal(0, _queue.Depth);
    }

    [Fact]
    public async Task PollOnce_ReadsBotTimeZoneFromMyselfWhenNotConfigured()
    {
        Stubs.Myself(_server, "UTC");
        Stubs.Search(_server, Fx.SearchResult());
        var poller = CreatePoller(TestOptions.For(_server, o => o.TimeZone = null));

        await poller.PollOnceAsync(_ct);
        await poller.PollOnceAsync(_ct);

        Assert.Equal(1, _server.Requests().Count(r => r.Path == "/rest/api/2/myself"));
        Assert.Equal(2, _server.Requests().Count(r => r.Path == "/rest/api/2/search"));
        Assert.NotNull(await _cursors.GetAsync("jira:PROJ", _ct));
    }

    [Fact]
    public async Task PollOnce_ConfiguredTimeZone_SkipsMyself()
    {
        Stubs.Search(_server, Fx.SearchResult());
        var poller = CreatePoller(TestOptions.For(_server, o => o.TimeZone = "UTC"));

        await poller.PollOnceAsync(_ct);

        Assert.DoesNotContain(_server.Requests(), r => r.Path == "/rest/api/2/myself");
    }

    [Fact]
    public async Task PollOnce_UnknownTimeZone_FallsBackToUtcAndStillPolls()
    {
        Stubs.Search(_server, Fx.SearchResult());
        var poller = CreatePoller(TestOptions.For(_server, o => o.TimeZone = "Nowhere/Invalid"));

        var delay = await poller.PollOnceAsync(_ct);

        Assert.Equal(TimeSpan.FromSeconds(1), delay);
        var search = _server.Requests().Single(r => r.Path == "/rest/api/2/search").JsonBody();
        Assert.Contains("2024-05-01 10:16", search.GetProperty("jql").GetString());
    }

    [Fact]
    public async Task PollOnce_MyselfFailure_StillPollsInUtc()
    {
        _server.Given(Request.Create().WithPath("/rest/api/2/myself").UsingGet()).RespondWith(Stubs.Error(503, "down"));
        Stubs.Search(_server, Fx.SearchResult());
        var poller = CreatePoller(TestOptions.For(_server, o => o.TimeZone = null));

        var delay = await poller.PollOnceAsync(_ct);

        Assert.Equal(TimeSpan.FromSeconds(1), delay);
        Assert.Single(_server.Requests(), r => r.Path == "/rest/api/2/search");
    }

    [Fact]
    public async Task PollOnce_NoProjects_DoesNothing()
    {
        var poller = CreatePoller(TestOptions.For(_server, o => o.Projects = []));

        var delay = await poller.PollOnceAsync(_ct);

        Assert.Equal(TimeSpan.FromSeconds(1), delay);
        Assert.Empty(_server.LogEntries);
    }

    [Fact]
    public async Task PollOnce_MultipleProjects_UseSeparateWatermarks()
    {
        _server.Given(Request.Create().WithPath("/rest/api/2/search").UsingPost().WithBody(new JsonPartialWildcardMatcher(new { jql = "project = \"PROJ\"*" })))
            .RespondWith(Stubs.Json(Fx.SearchResult(Fx.Issue("PROJ-1", "a", updated: Fx.T1018))));
        _server.Given(Request.Create().WithPath("/rest/api/2/search").UsingPost().WithBody(new JsonPartialWildcardMatcher(new { jql = "project = \"OPS\"*" })))
            .RespondWith(Stubs.Json(Fx.SearchResult(Fx.Issue("OPS-9", "b", updated: Fx.T1019))));
        var poller = CreatePoller(TestOptions.For(_server, o => o.Projects = ["proj", "OPS"]));

        await poller.PollOnceAsync(_ct);

        Assert.Equal(Fx.At(Fx.T1018), JiraPoller.ParseWatermark(await _cursors.GetAsync("jira:PROJ", _ct)));
        Assert.Equal(Fx.At(Fx.T1019), JiraPoller.ParseWatermark(await _cursors.GetAsync("jira:OPS", _ct)));
    }

    [Fact]
    public async Task ExecuteAsync_RunsUntilStopped()
    {
        Stubs.Search(_server, Fx.SearchResult(SampleIssues()));
        var poller = CreatePoller(TestOptions.For(_server));

        await poller.StartAsync(_ct);
        var events = await Queues.DrainAsync(_queue, 2, _ct);
        await poller.StopAsync(_ct);

        Assert.Equal(2, events.Count);
        Assert.Contains("last poll", poller.Status);
        Assert.Equal("Jira poller", poller.Name);
    }

    [Fact]
    public void BuildJql_RendersCutoffInTheGivenZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+02", TimeSpan.FromHours(2), "Test", "Test");
        var cutoff = new DateTimeOffset(2024, 5, 1, 10, 16, 45, TimeSpan.Zero);

        var jql = JiraPoller.BuildJql("PROJ", cutoff, zone);

        Assert.Equal("project = \"PROJ\" AND updated >= \"2024-05-01 12:16\" ORDER BY updated ASC", jql);
    }

    [Fact]
    public void Watermark_RoundTrips()
    {
        var value = new DateTimeOffset(2024, 5, 1, 12, 19, 0, TimeSpan.FromHours(2));

        var text = JiraPoller.FormatWatermark(value);

        Assert.Equal("2024-05-01T10:19:00.0000000+00:00", text);
        Assert.Equal(value, JiraPoller.ParseWatermark(text));
        Assert.Null(JiraPoller.ParseWatermark(null));
        Assert.Null(JiraPoller.ParseWatermark("garbage"));
    }

    [Fact]
    public void BuildFields_AppendsRepositoryField()
    {
        Assert.DoesNotContain("customfield_1", JiraPoller.BuildFields(TestOptions.Plain()));
        Assert.Contains("customfield_1", JiraPoller.BuildFields(TestOptions.Plain(o => o.RepositoryField = " customfield_1 ")));
    }
}
