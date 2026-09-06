using System.Text.Json;
using Agent.Channels.GitLab;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using Xunit;

namespace Agent.Channels.GitLab.Tests;

public sealed class GitLabClientTests : IDisposable
{
    private readonly GitLabTestServer _gitlab = new();

    public void Dispose() => _gitlab.Dispose();

    [Fact]
    public async Task GetCurrentUserAsync_SendsPrivateTokenHeader_AndParsesUser()
    {
        _gitlab.Get("/api/v4/user", Payloads.User(7, "agent-bot", "bot@example.test"));

        var user = await _gitlab.Client.GetCurrentUserAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, user.Id);
        Assert.Equal("agent-bot", user.Username);
        var request = Assert.Single(_gitlab.Requests("GET", "/api/v4/user"));
        Assert.Equal(GitLabTestServer.Token, request.Headers!["PRIVATE-TOKEN"].Single());
    }

    [Fact]
    public async Task GetProjectAsync_WithPath_UrlEncodesTheSlash()
    {
        _gitlab.Get("/api/v4/projects/team/repo", Payloads.Project(42, "team/repo"));

        var project = await _gitlab.Client.GetProjectAsync("team/repo", TestContext.Current.CancellationToken);

        Assert.NotNull(project);
        Assert.Equal(42, project.Id);
        Assert.Equal("main", project.DefaultBranch);
        Assert.Equal("https://gitlab.test/team/repo.git", project.HttpUrlToRepo);
        var request = Assert.Single(_gitlab.Requests("GET", "/api/v4/projects/"));
        Assert.Contains("team%2Frepo", request.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetProjectAsync_NotFound_ReturnsNull()
    {
        _gitlab.GetStatus("/api/v4/projects/999", 404);

        var project = await _gitlab.Client.GetProjectAsync("999", TestContext.Current.CancellationToken);

        Assert.Null(project);
    }

    [Fact]
    public async Task GetProjectAsync_ServerError_ThrowsGitLabApiException()
    {
        _gitlab.GetStatus("/api/v4/projects/1", 500, "{\"message\":\"boom\"}");

        var ex = await Assert.ThrowsAsync<GitLabApiException>(() => _gitlab.Client.GetProjectAsync("1", TestContext.Current.CancellationToken));

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Contains("boom", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetUserAsync_ParsesEmailAndIdentities()
    {
        _gitlab.Get("/api/v4/users/5", Payloads.User(5, "alice", "alice@example.test", "CN=alice,OU=Users,DC=corp,DC=local"));

        var user = await _gitlab.Client.GetUserAsync(5, TestContext.Current.CancellationToken);

        Assert.NotNull(user);
        Assert.Equal("alice@example.test", user.Email);
        var identity = Assert.Single(user.Identities!);
        Assert.Equal("ldapmain", identity.Provider);
        Assert.Equal("CN=alice,OU=Users,DC=corp,DC=local", identity.ExternUid);
    }

    [Fact]
    public async Task GetUserAsync_NotFound_ReturnsNull()
    {
        _gitlab.GetStatus("/api/v4/users/404", 404);

        Assert.Null(await _gitlab.Client.GetUserAsync(404, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetUserByUsernameAsync_QueriesByUsername_AndPrefersExactMatch()
    {
        _gitlab.Get("/api/v4/users", new[] { Payloads.User(9, "bob-smith"), Payloads.User(8, "bob") }, ("username", "bob"));

        var user = await _gitlab.Client.GetUserByUsernameAsync("bob", TestContext.Current.CancellationToken);

        Assert.NotNull(user);
        Assert.Equal(8, user.Id);
    }

    [Fact]
    public async Task GetUserByUsernameAsync_NoMatch_ReturnsNull()
    {
        _gitlab.Get("/api/v4/users", Array.Empty<object>(), ("username", "nobody"));

        Assert.Null(await _gitlab.Client.GetUserByUsernameAsync("nobody", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetTodosAsync_RequestsPendingTodos_AndParsesThem()
    {
        var todo = Payloads.Todo(101, "mentioned", "Issue", Payloads.ProjectRef(42, "team/repo"), Payloads.UserRef(5, "alice"),
            Payloads.IssueTarget(12, 42, "team/repo", "Fix login", "Users cannot log in", Payloads.UserRef(5, "alice")),
            "https://gitlab.test/team/repo/-/issues/12#note_555", "@agent-bot what causes this?");
        _gitlab.Get("/api/v4/todos", new[] { todo }, ("state", "pending"));

        var todos = await _gitlab.Client.GetTodosAsync(TestContext.Current.CancellationToken);

        var parsed = Assert.Single(todos);
        Assert.Equal(101, parsed.Id);
        Assert.Equal("mentioned", parsed.ActionName);
        Assert.Equal("Issue", parsed.TargetType);
        Assert.Equal("team/repo", parsed.Project!.PathWithNamespace);
        Assert.Equal(12, parsed.Target!.Iid);
        Assert.Equal("alice", parsed.Author!.Username);
        Assert.Equal("@agent-bot what causes this?", parsed.Body);
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero), parsed.CreatedAt);
        var request = Assert.Single(_gitlab.Requests("GET", "/api/v4/todos"));
        Assert.Equal("100", request.Query!["per_page"].Single());
    }

    [Fact]
    public async Task MarkTodoDoneAsync_PostsToMarkAsDone()
    {
        _gitlab.Post("/api/v4/todos/101/mark_as_done", new { Id = 101, State = "done" }, 200);

        await _gitlab.Client.MarkTodoDoneAsync(101, TestContext.Current.CancellationToken);

        Assert.Single(_gitlab.Requests("POST", "/api/v4/todos/101/mark_as_done"));
    }

    [Fact]
    public async Task GetGroupIssuesAsync_FollowsXNextPageHeader()
    {
        var author = Payloads.UserRef(5, "alice");
        _gitlab.Server.Given(Request.Create().WithPath("/api/v4/groups/team/issues").WithParam("page", "1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithHeader("X-Next-Page", "2")
                .WithBody(Json.Of(new[] { Payloads.Issue(1, 42, "team/repo", "One", null, author, "2026-09-04T08:00:00.000Z") })));
        _gitlab.Server.Given(Request.Create().WithPath("/api/v4/groups/team/issues").WithParam("page", "2").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithHeader("X-Next-Page", "")
                .WithBody(Json.Of(new[] { Payloads.Issue(2, 42, "team/repo", "Two", null, author, "2026-09-04T08:05:00.000Z") })));

        var issues = await _gitlab.Client.GetGroupIssuesAsync("team", "agent", new DateTimeOffset(2026, 9, 4, 7, 0, 0, TimeSpan.Zero), "opened", TestContext.Current.CancellationToken);

        Assert.Equal([1L, 2L], issues.Select(i => i.Iid));
        Assert.Equal(2, _gitlab.Requests("GET", "/api/v4/groups/team/issues").Count);
        var first = _gitlab.Requests("GET", "/api/v4/groups/team/issues")[0].Query!;
        Assert.Equal("agent", first["labels"].Single());
        Assert.Equal("opened", first["state"].Single());
        Assert.Equal("updated_at", first["order_by"].Single());
        Assert.Equal("asc", first["sort"].Single());
        Assert.Equal("2026-09-04T07:00:00Z", first["updated_after"].Single());
    }

    [Fact]
    public async Task GetGroupIssuesAsync_WithNumericGroupAndNoWatermark_OmitsUpdatedAfter()
    {
        _gitlab.Get("/api/v4/groups/17/issues", Array.Empty<object>());

        var issues = await _gitlab.Client.GetGroupIssuesAsync("17", "agent", null, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(issues);
        var query = Assert.Single(_gitlab.Requests("GET", "/api/v4/groups/17/issues")).Query!;
        Assert.False(query.ContainsKey("updated_after"));
    }

    [Fact]
    public async Task GetIssueAsync_ParsesIssue()
    {
        _gitlab.Get("/api/v4/projects/42/issues/12", Payloads.Issue(12, 42, "team/repo", "Fix login", "Body", Payloads.UserRef(5, "alice"), "2026-09-04T08:00:00.000Z"));

        var issue = await _gitlab.Client.GetIssueAsync("42", 12, TestContext.Current.CancellationToken);

        Assert.NotNull(issue);
        Assert.Equal("Fix login", issue.Title);
        Assert.Equal("team/repo#12", issue.References!.Full);
        Assert.Equal(["agent"], issue.Labels!);
    }

    [Fact]
    public async Task GetIssueNotesAsync_RequestsAscendingByCreation_AndParsesSystemFlag()
    {
        _gitlab.Get("/api/v4/projects/42/issues/12/notes", new[]
        {
            Payloads.Note(1, "assigned to @agent-bot", Payloads.UserRef(5, "alice"), "2026-09-04T08:00:00.000Z", system: true),
            Payloads.Note(2, "Any idea?", Payloads.UserRef(5, "alice"), "2026-09-04T08:01:00.000Z"),
        });

        var notes = await _gitlab.Client.GetIssueNotesAsync("42", 12, TestContext.Current.CancellationToken);

        Assert.Equal(2, notes.Count);
        Assert.True(notes[0].System);
        Assert.False(notes[1].System);
        var query = Assert.Single(_gitlab.Requests("GET", "/issues/12/notes")).Query!;
        Assert.Equal("asc", query["sort"].Single());
        Assert.Equal("created_at", query["order_by"].Single());
    }

    [Fact]
    public async Task GetMergeRequestAsync_ParsesBranchesStateAndMergedAt()
    {
        _gitlab.Get("/api/v4/projects/42/merge_requests/7", Payloads.MergeRequest(7, 42, "team/repo", "Add feature", "merged", "agent/12-fix", "main", Payloads.UserRef(7, "agent-bot")));

        var mr = await _gitlab.Client.GetMergeRequestAsync("42", 7, TestContext.Current.CancellationToken);

        Assert.NotNull(mr);
        Assert.Equal("merged", mr.State);
        Assert.Equal("agent/12-fix", mr.SourceBranch);
        Assert.Equal("main", mr.TargetBranch);
        Assert.Equal("https://gitlab.test/team/repo/-/merge_requests/7", mr.WebUrl);
        Assert.NotNull(mr.MergedAt);
    }

    [Fact]
    public async Task GetMergeRequestNotesAsync_ReturnsNotes()
    {
        _gitlab.Get("/api/v4/projects/42/merge_requests/7/notes", new[] { Payloads.Note(3, "Looks good", Payloads.UserRef(5, "alice"), "2026-09-04T08:00:00.000Z") });

        var notes = await _gitlab.Client.GetMergeRequestNotesAsync("42", 7, TestContext.Current.CancellationToken);

        Assert.Equal("Looks good", Assert.Single(notes).Body);
    }

    [Fact]
    public async Task GetMergeRequestDiscussionsAsync_ParsesDiffPositions()
    {
        _gitlab.Get("/api/v4/projects/42/merge_requests/7/discussions", new[]
        {
            Payloads.Discussion("abc123", false,
                Payloads.Note(9, "Rename this", Payloads.UserRef(5, "alice"), "2026-09-04T08:00:00.000Z", position: Payloads.Position("src/Program.cs", 17), type: "DiffNote")),
            Payloads.Discussion("def456", true, Payloads.Note(10, "General remark", Payloads.UserRef(5, "alice"), "2026-09-04T08:01:00.000Z")),
        });

        var discussions = await _gitlab.Client.GetMergeRequestDiscussionsAsync("42", 7, TestContext.Current.CancellationToken);

        Assert.Equal(2, discussions.Count);
        var diff = discussions[0];
        Assert.Equal("abc123", diff.Id);
        Assert.False(diff.IndividualNote);
        var note = Assert.Single(diff.Notes);
        Assert.Equal("src/Program.cs", note.Position!.NewPath);
        Assert.Equal(17, note.Position.NewLine);
        Assert.True(discussions[1].IndividualNote);
    }

    [Fact]
    public async Task CreateIssueNoteAsync_PostsBodyAsJson()
    {
        _gitlab.Post("/api/v4/projects/team/repo/issues/12/notes", Payloads.Note(77, "Hello", Payloads.UserRef(7, "agent-bot"), "2026-09-04T08:00:00.000Z"));

        var note = await _gitlab.Client.CreateIssueNoteAsync("team/repo", 12, "Hello", TestContext.Current.CancellationToken);

        Assert.Equal(77, note.Id);
        var request = Assert.Single(_gitlab.Requests("POST", "/issues/12/notes"));
        Assert.Contains("team%2Frepo", request.Url, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("Hello", body.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task CreateMergeRequestNoteAsync_PostsToMergeRequestNotes()
    {
        _gitlab.Post("/api/v4/projects/42/merge_requests/7/notes", Payloads.Note(78, "Done", Payloads.UserRef(7, "agent-bot"), "2026-09-04T08:00:00.000Z"));

        var note = await _gitlab.Client.CreateMergeRequestNoteAsync("42", 7, "Done", TestContext.Current.CancellationToken);

        Assert.Equal(78, note.Id);
        Assert.Single(_gitlab.Requests("POST", "/merge_requests/7/notes"));
    }

    [Fact]
    public async Task CreateDiscussionReplyAsync_PostsToDiscussionNotes()
    {
        _gitlab.Post("/api/v4/projects/42/merge_requests/7/discussions/abc123/notes", Payloads.Note(79, "Renamed", Payloads.UserRef(7, "agent-bot"), "2026-09-04T08:00:00.000Z"));

        var note = await _gitlab.Client.CreateDiscussionReplyAsync("42", 7, "abc123", "Renamed", TestContext.Current.CancellationToken);

        Assert.Equal(79, note.Id);
        var request = Assert.Single(_gitlab.Requests("POST", "/discussions/abc123/notes"));
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("Renamed", body.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public async Task AwardEmojiOnNoteAsync_PostsEmojiName()
    {
        _gitlab.Post("/api/v4/projects/42/issues/12/notes/555/award_emoji", new { Id = 1, Name = "eyes" });

        await _gitlab.Client.AwardEmojiOnNoteAsync("42", GitLabNoteableType.Issue, 12, 555, "eyes", TestContext.Current.CancellationToken);

        var request = Assert.Single(_gitlab.Requests("POST", "/notes/555/award_emoji"));
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("eyes", body.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task AwardEmojiAsync_OnMergeRequest_UsesMergeRequestsSegment()
    {
        _gitlab.Post("/api/v4/projects/42/merge_requests/7/award_emoji", new { Id = 2, Name = "white_check_mark" });

        await _gitlab.Client.AwardEmojiAsync("42", GitLabNoteableType.MergeRequest, 7, "white_check_mark", TestContext.Current.CancellationToken);

        Assert.Single(_gitlab.Requests("POST", "/merge_requests/7/award_emoji"));
    }

    [Fact]
    public async Task ListMergeRequestsAsync_FiltersBySourceBranchAndState()
    {
        _gitlab.Get("/api/v4/projects/42/merge_requests", new[] { Payloads.MergeRequest(7, 42, "team/repo", "Add feature", "opened", "agent/12-fix", "main", Payloads.UserRef(7, "agent-bot")) },
            ("source_branch", "agent/12-fix"), ("state", "opened"));

        var mrs = await _gitlab.Client.ListMergeRequestsAsync("42", "agent/12-fix", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(7, Assert.Single(mrs).Iid);
    }

    [Fact]
    public async Task CreateMergeRequestAsync_SendsSnakeCasePayloadWithCommaSeparatedLabels()
    {
        _gitlab.Post("/api/v4/projects/42/merge_requests", Payloads.MergeRequest(8, 42, "team/repo", "Add feature", "opened", "agent/12-fix", "main", Payloads.UserRef(7, "agent-bot")));

        var mr = await _gitlab.Client.CreateMergeRequestAsync("42", new CreateMergeRequestRequest
        {
            SourceBranch = "agent/12-fix",
            TargetBranch = "main",
            Title = "Add feature",
            Description = "Closes #12",
            ReviewerIds = [5],
            Labels = ["agent", "bot"],
        }, TestContext.Current.CancellationToken);

        Assert.Equal(8, mr.Iid);
        var request = Assert.Single(_gitlab.Requests("POST", "/api/v4/projects/42/merge_requests"));
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal("agent/12-fix", root.GetProperty("source_branch").GetString());
        Assert.Equal("main", root.GetProperty("target_branch").GetString());
        Assert.Equal("Add feature", root.GetProperty("title").GetString());
        Assert.Equal("Closes #12", root.GetProperty("description").GetString());
        Assert.True(root.GetProperty("remove_source_branch").GetBoolean());
        Assert.Equal(5, root.GetProperty("reviewer_ids")[0].GetInt64());
        Assert.Equal("agent,bot", root.GetProperty("labels").GetString());
    }

    [Fact]
    public async Task UpdateMergeRequestAsync_PutsTitleDescriptionAndLabels()
    {
        _gitlab.Put("/api/v4/projects/42/merge_requests/8", Payloads.MergeRequest(8, 42, "team/repo", "New title", "opened", "agent/12-fix", "main", Payloads.UserRef(7, "agent-bot")));

        var mr = await _gitlab.Client.UpdateMergeRequestAsync("42", 8, new UpdateMergeRequestRequest { Title = "New title", Description = "Updated", Labels = ["agent"] }, TestContext.Current.CancellationToken);

        Assert.Equal("New title", mr.Title);
        var request = Assert.Single(_gitlab.Requests("PUT", "/merge_requests/8"));
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("New title", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("Updated", body.RootElement.GetProperty("description").GetString());
        Assert.Equal("agent", body.RootElement.GetProperty("labels").GetString());
        Assert.False(body.RootElement.TryGetProperty("reviewer_ids", out _));
    }

    [Fact]
    public async Task GetFileAsync_EncodesPathAndPassesRef()
    {
        _gitlab.Server.Given(Request.Create().WithPath("/api/v4/projects/team/repo/repository/files/src/Program.cs/raw").WithParam("ref", "main").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/plain").WithBody("Console.WriteLine(\"hi\");"));

        var content = await _gitlab.Client.GetFileAsync("team/repo", "src/Program.cs", "main", TestContext.Current.CancellationToken);

        Assert.Equal("Console.WriteLine(\"hi\");", content);
        var request = Assert.Single(_gitlab.Requests("GET", "/repository/files/"));
        Assert.Contains("team%2Frepo/repository/files/src%2FProgram.cs/raw", request.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFileAsync_NotFound_ReturnsNull()
    {
        _gitlab.GetStatus("/api/v4/projects/42/repository/files/missing.txt/raw", 404);

        Assert.Null(await _gitlab.Client.GetFileAsync("42", "missing.txt", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SearchBlobsAsync_UsesBlobScope_AndParsesHits()
    {
        _gitlab.Get("/api/v4/projects/42/search", new[]
        {
            new { Basename = "Program", Data = "var x = Login();\n", Path = "src/Program.cs", Filename = "src/Program.cs", Id = (string?)null, Ref = "main", Startline = 10, ProjectId = 42 },
        }, ("scope", "blobs"), ("search", "Login"));

        var blobs = await _gitlab.Client.SearchBlobsAsync("42", "Login", TestContext.Current.CancellationToken);

        var blob = Assert.Single(blobs);
        Assert.Equal("src/Program.cs", blob.Path);
        Assert.Equal(10, blob.Startline);
        Assert.Contains("Login()", blob.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetMergeRequestAsync_ParsesHeadPipeline()
    {
        _gitlab.Get(
            "/api/v4/projects/42/merge_requests/7",
            Payloads.MergeRequest(7, 42, "team/repo", "Add feature", "opened", "agent/12-fix", "main", Payloads.UserRef(7, "agent-bot"), headPipeline: Payloads.Pipeline(900, "failed")));

        var mr = await _gitlab.Client.GetMergeRequestAsync("42", 7, TestContext.Current.CancellationToken);

        Assert.NotNull(mr!.HeadPipeline);
        Assert.Equal(900, mr.HeadPipeline.Id);
        Assert.Equal("failed", mr.HeadPipeline.Status);
        Assert.True(mr.HeadPipeline.IsFailed);
        Assert.Equal("https://gitlab.test/team/repo/-/pipelines/900", mr.HeadPipeline.WebUrl);
        Assert.Equal("agent/12-fix", mr.HeadPipeline.Ref);
    }

    [Fact]
    public async Task GetMergeRequestAsync_WithoutPipeline_LeavesHeadPipelineNull()
    {
        _gitlab.Get("/api/v4/projects/42/merge_requests/7", Payloads.MergeRequest(7, 42, "team/repo", "Add feature", "opened", "agent/12-fix", "main", Payloads.UserRef(7, "agent-bot")));

        var mr = await _gitlab.Client.GetMergeRequestAsync("42", 7, TestContext.Current.CancellationToken);

        Assert.Null(mr!.HeadPipeline);
    }

    [Fact]
    public async Task GetPipelineJobsAsync_EncodesProjectPath_AndFollowsPaging()
    {
        _gitlab.Server.Given(Request.Create().WithPath("/api/v4/projects/team/repo/pipelines/900/jobs").WithParam("page", "1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithHeader("X-Next-Page", "2")
                .WithBody(Json.Of(new[] { Payloads.Job(1, "build", "success", "build") })));
        _gitlab.Server.Given(Request.Create().WithPath("/api/v4/projects/team/repo/pipelines/900/jobs").WithParam("page", "2").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithHeader("X-Next-Page", "")
                .WithBody(Json.Of(new[] { Payloads.Job(2, "test", "failed"), Payloads.Job(3, "lint", "failed", allowFailure: true) })));

        var jobs = await _gitlab.Client.GetPipelineJobsAsync("team/repo", 900, TestContext.Current.CancellationToken);

        Assert.Equal(["build", "test", "lint"], jobs.Select(j => j.Name));
        Assert.False(jobs[0].IsFailed);
        Assert.True(jobs[1].IsFailed);
        Assert.False(jobs[1].AllowFailure);
        Assert.True(jobs[2].AllowFailure);
        Assert.Equal("script_failure", jobs[1].FailureReason);
        var requests = _gitlab.Requests("GET", "/pipelines/900/jobs");
        Assert.Equal(2, requests.Count);
        Assert.Contains("team%2Frepo", requests[0].Url, StringComparison.Ordinal);
        Assert.Equal("100", requests[0].Query!["per_page"].Single());
    }

    [Fact]
    public async Task GetJobTraceTailAsync_ShortTrace_ReturnsItWhole()
    {
        _gitlab.GetText("/api/v4/projects/42/jobs/55/trace", "$ dotnet test\nFailed! 1 error\n");

        var trace = await _gitlab.Client.GetJobTraceTailAsync("42", 55, 4000, TestContext.Current.CancellationToken);

        Assert.Equal("$ dotnet test\nFailed! 1 error\n", trace);
        Assert.Single(_gitlab.Requests("GET", "/jobs/55/trace"));
    }

    [Fact]
    public async Task GetJobTraceTailAsync_LongTrace_KeepsOnlyTheTail()
    {
        _gitlab.GetText("/api/v4/projects/42/jobs/55/trace", new string('x', 50_000) + "ERROR: the build failed");

        var trace = await _gitlab.Client.GetJobTraceTailAsync("42", 55, 100, TestContext.Current.CancellationToken);

        Assert.Equal(101, trace.Length); // the ellipsis marks what was dropped
        Assert.StartsWith("…", trace, StringComparison.Ordinal);
        Assert.EndsWith("ERROR: the build failed", trace, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetJobTraceTailAsync_NoTrace_ReturnsEmpty()
    {
        _gitlab.GetStatus("/api/v4/projects/42/jobs/55/trace", 404);

        Assert.Equal(string.Empty, await _gitlab.Client.GetJobTraceTailAsync("42", 55, 4000, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetJobTraceTailAsync_ServerError_Throws()
    {
        _gitlab.GetStatus("/api/v4/projects/42/jobs/55/trace", 500, "{\"message\":\"boom\"}");

        var ex = await Assert.ThrowsAsync<GitLabApiException>(() => _gitlab.Client.GetJobTraceTailAsync("42", 55, 4000, TestContext.Current.CancellationToken));

        Assert.Contains("boom", ex.Message, StringComparison.Ordinal);
    }
}
