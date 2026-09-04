using Agent.Channels.GitLab;
using Agent.Core.Authorization;
using Agent.Core.Tools;
using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace Agent.Channels.GitLab.Tests;

public sealed class GitLabToolsTests
{
    private static readonly GitLabUserRef Alice = new() { Id = 5, Username = "alice" };

    private readonly IGitLabClient _client = Substitute.For<IGitLabClient>();

    private GitLabTools CreateTools() => new(_client, Loggers.For<GitLabTools>());

    private static string AsText(object? result) => result switch
    {
        string s => s,
        System.Text.Json.JsonElement e when e.ValueKind == System.Text.Json.JsonValueKind.String => e.GetString()!,
        _ => throw new Xunit.Sdk.XunitException($"Unexpected tool result type {result?.GetType().Name ?? "null"}"),
    };

    private static GitLabNote Note(long id, string body, DateTimeOffset at, bool system = false)
        => new() { Id = id, Body = body, Author = Alice, CreatedAt = at, System = system };

    [Fact]
    public void GetTools_ExposesFourNativeReadOnlyTools()
    {
        var tools = CreateTools().GetTools().ToList();

        Assert.Equal(["gitlab_get_issue", "gitlab_get_mr", "gitlab_search_code", "gitlab_read_file"], tools.Select(t => t.Name));
        Assert.All(tools, t =>
        {
            Assert.Equal(Role.Users, t.Role);
            Assert.Equal(ToolScope.All, t.Scope);
            Assert.Equal("native", t.Source);
            Assert.Null(t.Channels);
            Assert.False(string.IsNullOrWhiteSpace(t.Function.Description));
        });
    }

    [Fact]
    public async Task GetIssue_ViaAIFunction_FormatsIssueWithLastFiveNotes()
    {
        _client.GetIssueAsync("team/repo", 12, Arg.Any<CancellationToken>()).Returns(new GitLabIssue
        {
            Iid = 12, ProjectId = 42, Title = "Fix login", Description = "Users cannot log in", State = "opened", Author = Alice, Labels = ["agent", "bug"],
            WebUrl = "https://gitlab.test/team/repo/-/issues/12", UpdatedAt = new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero),
            References = new GitLabReferences { Full = "team/repo#12" },
        });
        var start = new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero);
        var notes = Enumerable.Range(1, 7).Select(i => Note(i, $"note {i}", start.AddMinutes(i))).Append(Note(99, "changed the description", start.AddMinutes(10), system: true)).ToArray();
        _client.GetIssueNotesAsync("team/repo", 12, Arg.Any<CancellationToken>()).Returns(notes);
        var tool = CreateTools().GetTools().Single(t => t.Name == "gitlab_get_issue").Function;

        var result = AsText(await tool.InvokeAsync(new AIFunctionArguments { ["project"] = "team/repo", ["iid"] = 12 }, TestContext.Current.CancellationToken));

        Assert.Contains("Issue team/repo#12: Fix login", result, StringComparison.Ordinal);
        Assert.Contains("State: opened", result, StringComparison.Ordinal);
        Assert.Contains("Labels: agent, bug", result, StringComparison.Ordinal);
        Assert.Contains("Users cannot log in", result, StringComparison.Ordinal);
        Assert.Contains("Last 5 comment(s):", result, StringComparison.Ordinal);
        Assert.DoesNotContain("note 1\n", result, StringComparison.Ordinal);
        Assert.DoesNotContain("note 2\n", result, StringComparison.Ordinal);
        Assert.Contains("note 3", result, StringComparison.Ordinal);
        Assert.Contains("note 7", result, StringComparison.Ordinal);
        Assert.DoesNotContain("changed the description", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetIssue_NotFound_ReturnsErrorText()
    {
        _client.GetIssueAsync("team/repo", 404, Arg.Any<CancellationToken>()).Returns((GitLabIssue?)null);

        var result = await CreateTools().GetIssueAsync("team/repo", 404, TestContext.Current.CancellationToken);

        Assert.StartsWith("Error:", result, StringComparison.Ordinal);
        Assert.Contains("team/repo#404", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetIssue_ClientThrows_ReturnsErrorTextInsteadOfThrowing()
    {
        _client.GetIssueAsync("team/repo", 12, Arg.Any<CancellationToken>()).ThrowsAsync(new GitLabApiException(System.Net.HttpStatusCode.Forbidden, "denied", null));

        var result = await CreateTools().GetIssueAsync("team/repo", 12, TestContext.Current.CancellationToken);

        Assert.StartsWith("Error:", result, StringComparison.Ordinal);
        Assert.Contains("403", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetMergeRequest_FormatsStateBranchesAndNotes()
    {
        _client.GetMergeRequestAsync("42", 7, Arg.Any<CancellationToken>()).Returns(new GitLabMergeRequest
        {
            Iid = 7, Title = "Add feature", State = "opened", Draft = true, SourceBranch = "agent/12-fix", TargetBranch = "main", Author = Alice, Labels = ["agent"], Description = "Closes #12", WebUrl = "https://gitlab.test/team/repo/-/merge_requests/7",
        });
        _client.GetMergeRequestNotesAsync("42", 7, Arg.Any<CancellationToken>()).Returns(new[] { Note(1, "LGTM", new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero)) });

        var result = await CreateTools().GetMergeRequestAsync("42", 7, TestContext.Current.CancellationToken);

        Assert.Contains("Merge request 42!7: Add feature", result, StringComparison.Ordinal);
        Assert.Contains("State: opened (draft)", result, StringComparison.Ordinal);
        Assert.Contains("Branches: agent/12-fix -> main", result, StringComparison.Ordinal);
        Assert.Contains("Closes #12", result, StringComparison.Ordinal);
        Assert.Contains("[alice, 2026-09-04 08:00:00Z] LGTM", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchCode_ListsTopTenWithSnippets()
    {
        var blobs = Enumerable.Range(1, 12).Select(i => new GitLabBlob { Path = $"src/File{i}.cs", Data = $"line with Login {i}\n", Startline = i, Ref = "main" }).ToArray();
        _client.SearchBlobsAsync("42", "Login", Arg.Any<CancellationToken>()).Returns(blobs);

        var result = await CreateTools().SearchCodeAsync("42", "Login", TestContext.Current.CancellationToken);

        Assert.Contains("12 match(es) in 42; showing 10:", result, StringComparison.Ordinal);
        Assert.Contains("## src/File1.cs (line 1) @ main", result, StringComparison.Ordinal);
        Assert.Contains("line with Login 10", result, StringComparison.Ordinal);
        Assert.DoesNotContain("src/File11.cs", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchCode_NoHits_SaysSo()
    {
        _client.SearchBlobsAsync("42", "nothing", Arg.Any<CancellationToken>()).Returns(Array.Empty<GitLabBlob>());

        Assert.Equal("No files in 42 match \"nothing\".", await CreateTools().SearchCodeAsync("42", "nothing", TestContext.Current.CancellationToken));
        Assert.StartsWith("Error:", await CreateTools().SearchCodeAsync("42", " ", TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadFile_WithoutRef_UsesDefaultBranch()
    {
        _client.GetProjectAsync("team/repo", Arg.Any<CancellationToken>()).Returns(new GitLabProject { Id = 42, DefaultBranch = "develop" });
        _client.GetFileAsync("team/repo", "README.md", "develop", Arg.Any<CancellationToken>()).Returns("# Hello");

        var result = await CreateTools().ReadFileAsync("team/repo", "/README.md", null, TestContext.Current.CancellationToken);

        Assert.Equal("# Hello", result);
    }

    [Fact]
    public async Task ReadFile_WithRef_SkipsProjectLookup_AndTruncatesLongContent()
    {
        _client.GetFileAsync("42", "big.txt", "v1", Arg.Any<CancellationToken>()).Returns(new string('x', GitLabTools.MaxFileChars + 500));

        var result = await CreateTools().ReadFileAsync("42", "big.txt", "v1", TestContext.Current.CancellationToken);

        await _client.DidNotReceive().GetProjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.StartsWith(new string('x', 100), result, StringComparison.Ordinal);
        Assert.Contains("[truncated:", result, StringComparison.Ordinal);
        Assert.True(result.Length < GitLabTools.MaxFileChars + 200);
    }

    [Fact]
    public async Task ReadFile_Missing_ReturnsErrorText()
    {
        _client.GetFileAsync("42", "nope.txt", "main", Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await CreateTools().ReadFileAsync("42", "nope.txt", "main", TestContext.Current.CancellationToken);

        Assert.Equal("Error: file nope.txt was not found in 42 at main.", result);
    }
}
