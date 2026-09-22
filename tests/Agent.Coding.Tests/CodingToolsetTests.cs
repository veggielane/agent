using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Agent.Coding.Tests;

public sealed class CodingToolsetTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly CodingRunState _state = new();
    private readonly IGitRunner _git = Substitute.For<IGitRunner>();
    private readonly ListProgress<CodingAction> _actions = new();

    public void Dispose() => _dir.Dispose();

    private CodingToolset Create(Action<CodingOptions>? configure = null, RepoProfile? profile = null)
    {
        var options = TestOptions.Coding(_dir.Path, configure);
        var workspace = new Workspace(1, _dir.Path, _dir.Path, "agent/x", "main", profile ?? RepoProfile.Empty);
        var processes = new CliWrapProcessRunner(TestOptions.Monitor(options), NullLogger<CliWrapProcessRunner>.Instance);
        return new CodingToolset(workspace, options, processes, _git, _state, NullLogger.Instance, actions: _actions);
    }

    [Fact]
    public void CreateTools_ExposesAllNineTools()
    {
        var names = Create().CreateTools().Select(t => t.Name).ToList();

        Assert.Equal(["list_files", "read_file", "search", "write_file", "edit_file", "run", "git_diff", "git_status", "done"], names);
    }

    [Fact]
    public void ListFiles_SkipsGitBinObjAndHonoursGlob()
    {
        _dir.Write("src/A.cs", "a");
        _dir.Write("src/B.txt", "b");
        _dir.Write("bin/x.dll", "x");
        _dir.Write("obj/y.dll", "y");
        _dir.Write(".git/config", "z");
        _dir.Write("node_modules/m/index.js", "m");
        var tools = Create();

        var all = tools.ListFiles();
        var csOnly = tools.ListFiles("**/*.cs");

        Assert.Equal(["src/A.cs", "src/B.txt"], all.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("src/A.cs\n", csOnly);
    }

    [Fact]
    public void ListFiles_Max_TruncatesWithNote()
    {
        for (var i = 0; i < 5; i++)
        {
            _dir.Write($"f{i}.txt", "x");
        }

        var listed = Create().ListFiles(max: 2);

        Assert.Contains("f0.txt", listed, StringComparison.Ordinal);
        Assert.Contains("3 more", listed, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadFile_WholeFile_HasLineNumbers()
    {
        _dir.Write("a.txt", "one\ntwo\nthree\n");

        var text = Create().ReadFile("a.txt");

        Assert.Equal("1: one\n2: two\n3: three\n", text);
    }

    [Fact]
    public void ReadFile_LineRange_ClampsToFile()
    {
        _dir.Write("a.txt", "one\r\ntwo\r\nthree");

        var text = Create().ReadFile("a.txt", startLine: 2, endLine: 99);

        Assert.Equal("2: two\n3: three\n", text);
    }

    [Fact]
    public void ReadFile_Missing_ReturnsError()
    {
        Assert.StartsWith("error:", Create().ReadFile("nope.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadFile_Escape_ReturnsError()
    {
        var result = Create().ReadFile("../../etc/passwd");

        Assert.StartsWith("error:", result, StringComparison.Ordinal);
        Assert.Contains("outside", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadFile_Binary_ReturnsError()
    {
        File.WriteAllBytes(_dir.Combine("bin.dat"), [1, 2, 0, 3, 4]);

        Assert.Contains("binary", Create().ReadFile("bin.dat"), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadFile_LargeFile_TruncatedWithHint()
    {
        _dir.Write("big.txt", string.Join('\n', Enumerable.Range(1, 500).Select(i => $"line {i} " + new string('x', 50))));

        var text = Create(o => o.MaxFileReadChars = 1000).ReadFile("big.txt");

        Assert.Contains("startLine/endLine", text, StringComparison.Ordinal);
        Assert.True(text.Length < 1200);
    }

    [Fact]
    public void Search_FindsMatchesAcrossFiles_WithGlobAndMax()
    {
        _dir.Write("src/A.cs", "class Foo {}\nclass Bar {}\n");
        _dir.Write("src/B.cs", "class Baz {}\n");
        _dir.Write("docs/readme.md", "class Foo mention\n");
        File.WriteAllBytes(_dir.Combine("blob.bin"), [0, 1, 2, (byte)'c', (byte)'l', (byte)'a', (byte)'s', (byte)'s']);
        var tools = Create();

        var all = tools.Search(@"class \w+");
        var csOnly = tools.Search("class", "**/*.cs");
        var limited = tools.Search("class", max: 1);

        Assert.Contains("src/A.cs:1: class Foo {}", all, StringComparison.Ordinal);
        Assert.Contains("src/A.cs:2: class Bar {}", all, StringComparison.Ordinal);
        Assert.Contains("docs/readme.md:1", all, StringComparison.Ordinal);
        Assert.DoesNotContain("blob.bin", all, StringComparison.Ordinal);
        Assert.DoesNotContain("readme", csOnly, StringComparison.Ordinal);
        Assert.Contains("stopped after 1", limited, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_InvalidRegex_ReturnsError()
    {
        Assert.StartsWith("error: invalid regular expression", Create().Search("("), StringComparison.Ordinal);
    }

    [Fact]
    public void Search_NoMatches_SaysSo()
    {
        _dir.Write("a.txt", "hello");

        Assert.Equal("(no matches)", Create().Search("zzz"));
    }

    [Fact]
    public void WriteFile_CreatesDirectoriesAndFile()
    {
        var result = Create().WriteFile("new/dir/file.txt", "content");

        Assert.StartsWith("wrote new/dir/file.txt", result, StringComparison.Ordinal);
        Assert.Equal("content", File.ReadAllText(_dir.Combine("new", "dir", "file.txt")));
    }

    [Theory]
    [InlineData(".gitlab-ci.yml")]
    [InlineData("deploy/app.yaml")]
    [InlineData(".git/hooks/pre-commit")]
    [InlineData("../escape.txt")]
    public void WriteFile_ProtectedOrEscaping_ReturnsErrorAndWritesNothing(string path)
    {
        var result = Create().WriteFile(path, "x");

        Assert.StartsWith("error:", result, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(_dir.Path, path))));
    }

    [Fact]
    public void WriteFile_PerRepoProtectedPath_ReturnsError()
    {
        var tools = Create(profile: RepoProfile.Empty with { ExtraProtectedPaths = ["secrets/**"] });

        Assert.StartsWith("error:", tools.WriteFile("secrets/x.txt", "x"), StringComparison.Ordinal);
    }

    [Fact]
    public void EditFile_UniqueMatch_Replaces()
    {
        _dir.Write("a.txt", "alpha\nbeta\ngamma\n");

        var result = Create().EditFile("a.txt", "beta", "BETA");

        Assert.Equal("edited a.txt", result);
        Assert.Equal("alpha\nBETA\ngamma\n", File.ReadAllText(_dir.Combine("a.txt")));
    }

    [Fact]
    public void EditFile_AmbiguousMatch_ReturnsErrorAndLeavesFile()
    {
        _dir.Write("a.txt", "x\nx\n");

        var result = Create().EditFile("a.txt", "x", "y");

        Assert.Contains("matches 2 places", result, StringComparison.Ordinal);
        Assert.Equal("x\nx\n", File.ReadAllText(_dir.Combine("a.txt")));
    }

    [Fact]
    public void EditFile_NoMatch_ReturnsError()
    {
        _dir.Write("a.txt", "abc");

        Assert.Contains("not found", Create().EditFile("a.txt", "zzz", "y"), StringComparison.Ordinal);
    }

    [Fact]
    public void EditFile_CrLfFile_WithLfOldText_StillMatchesAndKeepsCrLf()
    {
        _dir.Write("a.txt", "one\r\ntwo\r\nthree\r\n");

        var result = Create().EditFile("a.txt", "two\nthree", "2\n3");

        Assert.Equal("edited a.txt", result);
        Assert.Equal("one\r\n2\r\n3\r\n", File.ReadAllText(_dir.Combine("a.txt")));
    }

    [Fact]
    public void EditFile_ProtectedPath_ReturnsError()
    {
        _dir.Write(".gitlab-ci.yml", "stages: []");

        Assert.StartsWith("error:", Create().EditFile(".gitlab-ci.yml", "stages", "x"), StringComparison.Ordinal);
        Assert.Equal("stages: []", File.ReadAllText(_dir.Combine(".gitlab-ci.yml")));
    }

    [Fact]
    public async Task Run_AllowedCommand_ReturnsExitCodeAndOutputAndCounts()
    {
        GitTestHelper.SkipIfMissing();
        var tools = Create();

        var output = await tools.RunAsync("git --version", TestContext.Current.CancellationToken);

        Assert.StartsWith("exit code: 0", output, StringComparison.Ordinal);
        Assert.Contains("git version", output, StringComparison.Ordinal);
        Assert.Equal(1, _state.RunCount);
        Assert.Equal(["git --version"], _state.CommandsRun);
    }

    [Fact]
    public async Task Run_DisallowedCommand_ReturnsErrorWithoutRunning()
    {
        var output = await Create().RunAsync("curl http://example.com", TestContext.Current.CancellationToken);

        Assert.StartsWith("error:", output, StringComparison.Ordinal);
        Assert.Contains("not an allowed executable", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_BudgetExhausted_ReturnsError()
    {
        var tools = Create(o => o.Budget.MaxRuns = 1);
        _state.RunCount = 1;

        var output = await tools.RunAsync("git --version", TestContext.Current.CancellationToken);

        Assert.Contains("run budget", output, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteFile_ReportsThePathAsAWriteAction_NeverTheContent()
    {
        Create().WriteFile("src/new.txt", "secret content");

        var action = Assert.Single(_actions.Items);
        Assert.Equal(new CodingAction(CodingActionKind.Write, "src/new.txt"), action);
    }

    [Fact]
    public void WriteFile_Refused_ReportsNoAction()
    {
        Create().WriteFile(".gitlab-ci.yml", "x");

        Assert.Empty(_actions.Items);
    }

    [Fact]
    public void EditFile_ReportsThePathAsAnEditAction()
    {
        _dir.Write("src/a.txt", "one two three");

        Create().EditFile("src/a.txt", "two", "2");

        Assert.Equal([new CodingAction(CodingActionKind.Edit, "src/a.txt")], _actions.Items);
    }

    [Fact]
    public async Task Run_ReportsTheCommandWithItsOutcome()
    {
        GitTestHelper.SkipIfMissing();

        await Create().RunAsync("git --version", TestContext.Current.CancellationToken);

        Assert.Equal([new CodingAction(CodingActionKind.Run, "git --version (ok)")], _actions.Items);
    }

    [Fact]
    public async Task Run_FailingCommand_ReportsTheExitCode()
    {
        GitTestHelper.SkipIfMissing();

        // The temp directory is not a repository, so an allowed git command fails with a non-zero exit code.
        await Create().RunAsync("git status", TestContext.Current.CancellationToken);

        var action = Assert.Single(_actions.Items);
        Assert.Equal(CodingActionKind.Run, action.Kind);
        Assert.StartsWith("git status (failed, exit ", action.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_RejectedCommand_ReportsTheRejection()
    {
        await Create().RunAsync("curl http://example.com", TestContext.Current.CancellationToken);

        Assert.Equal([new CodingAction(CodingActionKind.Run, "curl http://example.com (rejected)")], _actions.Items);
    }

    [Fact]
    public void Done_SetsStateAndReturnsOk()
    {
        var result = Create().Done("Added the thing.");

        Assert.Equal("ok", result);
        Assert.True(_state.Completed);
        Assert.Equal("Added the thing.", _state.Summary);
    }

    [Fact]
    public async Task GitStatus_UsesGitRunner()
    {
        _git.StatusAsync(_dir.Path, Arg.Any<CancellationToken>()).Returns(" M a.txt\n");

        var status = await Create().GitStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(" M a.txt\n", status);
    }

    [Fact]
    public async Task GitDiff_IncludesUntrackedFiles()
    {
        _git.DiffAsync(_dir.Path, false, Arg.Any<CancellationToken>()).Returns("diff --git a/a.txt b/a.txt\n");
        _git.StatusAsync(_dir.Path, Arg.Any<CancellationToken>()).Returns(" M a.txt\n?? new.txt\n");

        var diff = await Create().GitDiffAsync(TestContext.Current.CancellationToken);

        Assert.Contains("diff --git", diff, StringComparison.Ordinal);
        Assert.Contains("Untracked files:\n  new.txt", diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitDiff_GitFailure_ReturnsErrorText()
    {
        _git.DiffAsync(_dir.Path, false, Arg.Any<CancellationToken>()).Returns<string>(_ => throw new GitException("diff", 128, "not a repo"));

        var diff = await Create().GitDiffAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith("error:", diff, StringComparison.Ordinal);
    }
}
