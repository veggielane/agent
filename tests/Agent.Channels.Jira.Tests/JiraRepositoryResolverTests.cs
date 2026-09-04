using Agent.Channels.Jira;
using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Events;
using Agent.Core.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using WireMock.RequestBuilders;
using WireMock.Server;

namespace Agent.Channels.Jira.Tests;

public sealed class JiraRepositoryResolverTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public void Dispose() => _server.Dispose();

    private JiraRepositoryResolver CreateResolver(JiraOptions options)
        => new(TestOptions.Client(_server, options), TestOptions.Monitor(options), NullLogger<JiraRepositoryResolver>.Instance);

    private static InboundEvent Event(string text, string? issue = "PROJ-1", Channel channel = Channel.Jira) => new()
    {
        Channel = channel,
        EventId = "e1",
        Caller = new CallerIdentity(channel, "bob"),
        ConversationId = issue ?? "conv",
        Text = text,
        Metadata = issue is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["issue"] = issue },
    };

    [Theory]
    [InlineData("Please fix https://gitlab.corp.local/team/repo asap", "https://gitlab.corp.local/team/repo", "team/repo")]
    [InlineData("repo: https://gitlab.corp.local/team/repo.git.", "https://gitlab.corp.local/team/repo.git", "team/repo")]
    [InlineData("see https://gitlab.corp.local/group/sub/repo/-/issues/12?x=1#note_3", "https://gitlab.corp.local/group/sub/repo", "group/sub/repo")]
    [InlineData("(https://gitlab.corp.local/team/repo)", "https://gitlab.corp.local/team/repo", "team/repo")]
    [InlineData("[link|https://gitlab.corp.local/team/repo]", "https://gitlab.corp.local/team/repo", "team/repo")]
    [InlineData("[text](https://gitlab.corp.local/team/repo)", "https://gitlab.corp.local/team/repo", "team/repo")]
    public async Task ResolveAsync_UrlInText_WinsWithoutFetchingTheIssue(string text, string url, string projectId)
    {
        var target = await CreateResolver(TestOptions.For(_server)).ResolveAsync(Event(text), _ct);

        Assert.NotNull(target);
        Assert.Equal(url, target.Url);
        Assert.Equal(projectId, target.ProjectId);
        Assert.Null(target.DefaultBranch);
        Assert.Empty(_server.LogEntries);
    }

    [Theory]
    [InlineData("https://gitlab.corp.local/onlyone")]
    [InlineData("https://intranet.corp.local/team/repo")]
    [InlineData("ftp://gitlab.corp.local/team/repo")]
    [InlineData("no url at all")]
    public async Task ResolveAsync_TextWithoutUsableUrl_FallsBackToProjectMap(string text)
    {
        Stubs.Issue(_server, "PROJ-1", Fx.Issue("PROJ-1", "x"));
        var options = TestOptions.For(_server, o => o.ProjectRepos["PROJ"] = "https://gitlab.corp.local/default/repo");

        var target = await CreateResolver(options).ResolveAsync(Event(text), _ct);

        Assert.Equal("https://gitlab.corp.local/default/repo", target?.Url);
        Assert.Equal("default/repo", target?.ProjectId);
        Assert.Single(_server.LogEntries);
    }

    [Fact]
    public async Task ResolveAsync_RepoHostHint_MatchesHostsWithoutGitlabInTheName()
    {
        var options = TestOptions.For(_server, o => o.RepoHostHint = "git.corp.local");

        var target = await CreateResolver(options).ResolveAsync(Event("https://git.corp.local/team/repo"), _ct);

        Assert.Equal("https://git.corp.local/team/repo", target?.Url);
    }

    [Fact]
    public async Task ResolveAsync_RepositoryField_IsUsedRegardlessOfHost()
    {
        Stubs.Issue(_server, "PROJ-1", Fx.Issue("PROJ-1", "x", extra: new Dictionary<string, object?> { ["customfield_12345"] = "https://scm.corp.local/team/repo.git" }));
        var options = TestOptions.For(_server, o =>
        {
            o.RepositoryField = "customfield_12345";
            o.ProjectRepos["PROJ"] = "https://gitlab.corp.local/default/repo";
        });

        var target = await CreateResolver(options).ResolveAsync(Event("no link"), _ct);

        Assert.Equal("https://scm.corp.local/team/repo.git", target?.Url);
        Assert.Equal("team/repo", target?.ProjectId);
        var request = Assert.Single(_server.Requests());
        Assert.Contains("customfield_12345", request.Query!["fields"].Single());
    }

    [Fact]
    public async Task ResolveAsync_RepositoryFieldAsOptionObject_IsRead()
    {
        Stubs.Issue(_server, "PROJ-1", Fx.Issue("PROJ-1", "x", extra: new Dictionary<string, object?> { ["customfield_1"] = new { id = "5", value = "https://gitlab.corp.local/opt/repo" } }));
        var options = TestOptions.For(_server, o => o.RepositoryField = "customfield_1");

        var target = await CreateResolver(options).ResolveAsync(Event("no link"), _ct);

        Assert.Equal("https://gitlab.corp.local/opt/repo", target?.Url);
    }

    [Fact]
    public async Task ResolveAsync_ComponentMap_BeatsProjectMapAndIgnoresCase()
    {
        Stubs.Issue(_server, "PROJ-1", Fx.Issue("PROJ-1", "x", components: ["Docs", "API"]));
        var options = TestOptions.For(_server, o =>
        {
            o.ComponentRepos["api"] = "https://gitlab.corp.local/team/api";
            o.ProjectRepos["PROJ"] = "https://gitlab.corp.local/default/repo";
        });

        var target = await CreateResolver(options).ResolveAsync(Event("no link"), _ct);

        Assert.Equal("https://gitlab.corp.local/team/api", target?.Url);
    }

    [Fact]
    public async Task ResolveAsync_NothingConfigured_ReturnsNull()
    {
        Stubs.Issue(_server, "PROJ-1", Fx.Issue("PROJ-1", "x", components: ["api"]));

        Assert.Null(await CreateResolver(TestOptions.For(_server)).ResolveAsync(Event("no link"), _ct));
    }

    [Fact]
    public async Task ResolveAsync_IssueNotFound_ReturnsNull()
    {
        _server.Given(Request.Create().WithPath("/rest/api/2/issue/PROJ-1").UsingGet()).RespondWith(Stubs.Error(404, "nope"));
        var options = TestOptions.For(_server, o => o.ProjectRepos["PROJ"] = "https://gitlab.corp.local/default/repo");

        Assert.Null(await CreateResolver(options).ResolveAsync(Event("no link"), _ct));
    }

    [Fact]
    public async Task ResolveAsync_JiraDown_ReturnsNullInsteadOfThrowing()
    {
        _server.Given(Request.Create().WithPath("/rest/api/2/issue/PROJ-1").UsingGet()).RespondWith(Stubs.Error(503, "down"));

        Assert.Null(await CreateResolver(TestOptions.For(_server)).ResolveAsync(Event("no link"), _ct));
    }

    [Fact]
    public async Task ResolveAsync_NonJiraEventWithoutIssue_DoesNotFetch()
    {
        var options = TestOptions.For(_server, o => o.ProjectRepos["PROJ"] = "https://gitlab.corp.local/default/repo");

        var target = await CreateResolver(options).ResolveAsync(Event("no link", issue: null, channel: Channel.Mattermost), _ct);

        Assert.Null(target);
        Assert.Empty(_server.LogEntries);
    }

    [Fact]
    public async Task ResolveAsync_TaskContextSourceRef_IsUsedWhenMetadataIsMissing()
    {
        Stubs.Issue(_server, "PROJ-5", Fx.Issue("PROJ-5", "x"));
        var options = TestOptions.For(_server, o => o.ProjectRepos["PROJ"] = "https://gitlab.corp.local/default/repo");
        var evt = Event("no link", issue: null, channel: Channel.Cli) with { Task = new TaskContext { Source = TaskSource.JiraIssue, SourceRef = "PROJ-5" } };

        var target = await CreateResolver(options).ResolveAsync(evt, _ct);

        Assert.Equal("https://gitlab.corp.local/default/repo", target?.Url);
    }

    [Fact]
    public void ResolveFromIssue_FollowsTheConfiguredOrder()
    {
        var options = TestOptions.Plain(o =>
        {
            o.RepositoryField = "customfield_1";
            o.ComponentRepos["api"] = "https://gitlab.corp.local/by/component";
            o.ProjectRepos["PROJ"] = "https://gitlab.corp.local/by/project";
        });
        var extra = new Dictionary<string, object?> { ["customfield_1"] = "https://gitlab.corp.local/by/field" };

        var all = Fx.Parse(Fx.Issue("PROJ-1", "x", description: "text https://gitlab.corp.local/by/description ok", components: ["api"], extra: extra));
        var noText = Fx.Parse(Fx.Issue("PROJ-1", "x", components: ["api"], extra: extra));
        var noField = Fx.Parse(Fx.Issue("PROJ-1", "x", components: ["api"]));
        var noComponent = Fx.Parse(Fx.Issue("PROJ-1", "x"));

        Assert.Equal("https://gitlab.corp.local/by/description", JiraRepositoryResolver.ResolveFromIssue(all, options)?.Url);
        Assert.Equal("https://gitlab.corp.local/by/field", JiraRepositoryResolver.ResolveFromIssue(noText, options)?.Url);
        Assert.Equal("https://gitlab.corp.local/by/component", JiraRepositoryResolver.ResolveFromIssue(noField, options)?.Url);
        Assert.Equal("https://gitlab.corp.local/by/project", JiraRepositoryResolver.ResolveFromIssue(noComponent, options)?.Url);
    }

    [Fact]
    public void ResolveFromIssue_ProjectKeyFallsBackToIssueKeyPrefix()
    {
        var options = TestOptions.Plain(o => o.ProjectRepos["OPS"] = "https://gitlab.corp.local/ops/repo");
        var issue = new JiraIssue { Key = "OPS-12", Fields = new JiraFields() };

        Assert.Equal("https://gitlab.corp.local/ops/repo", JiraRepositoryResolver.ResolveFromIssue(issue, options)?.Url);
        Assert.Equal("OPS", JiraRepositoryResolver.ProjectKeyOf("OPS-12"));
        Assert.Null(JiraRepositoryResolver.ProjectKeyOf("nodash"));
    }

    [Fact]
    public void ResolveFromIssue_ProjectMapLookupIgnoresCaseEvenForOrdinalDictionaries()
    {
        var options = TestOptions.Plain(o => o.ProjectRepos = new Dictionary<string, string>(StringComparer.Ordinal) { ["proj"] = "https://gitlab.corp.local/x/y" });

        Assert.Equal("x/y", JiraRepositoryResolver.ResolveFromIssue(Fx.Parse(Fx.Issue("PROJ-1", "x")), options)?.ProjectId);
    }

    [Fact]
    public void ToTarget_StripsGitSuffixFromProjectIdOnly()
    {
        var target = JiraRepositoryResolver.ToTarget("https://gitlab.corp.local/a/b/c.git");

        Assert.Equal("https://gitlab.corp.local/a/b/c.git", target.Url);
        Assert.Equal("a/b/c", target.ProjectId);
        Assert.Null(JiraRepositoryResolver.ToTarget("https://gitlab.corp.local/").ProjectId);
    }
}
