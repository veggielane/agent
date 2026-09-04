using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Llm;
using Agent.Core.Prompts;
using Agent.Core.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Agent.Coding.Tests;

public sealed class CodingEngineTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly IToolRegistry _registry = Substitute.For<IToolRegistry>();
    private readonly CallerIdentity _requester = CallerIdentity.Local("alice", Role.Team);

    public CodingEngineTests()
    {
        _registry.GetTools(Arg.Any<CallerIdentity>(), Arg.Any<ToolScope>(), Arg.Any<Channel>(), Arg.Any<IReadOnlyCollection<string>?>())
            .Returns(Array.Empty<ToolDescriptor>());
    }

    public void Dispose() => _dir.Dispose();

    private (CodingEngine Engine, Workspace Workspace) Build(ScriptedChatClient script, Action<CodingOptions>? configure = null, RepoProfile? profile = null)
    {
        var repo = _dir.Combine("repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "README.md"), "# Repo\n");

        var options = TestOptions.Coding(_dir.Path, configure);
        var monitor = TestOptions.Monitor(options);
        var client = new ChatClientBuilder(script).UseFunctionInvocation().Build();

        var factory = Substitute.For<IChatClientFactory>();
        factory.Create(ModelPurpose.Coding, Arg.Any<string?>(), Arg.Any<string?>()).Returns(client);

        var prompts = Substitute.For<IPromptProvider>();
        prompts.GetCodingPrompt().Returns(FilePromptProvider.DefaultCodingPrompt);

        var processes = new CliWrapProcessRunner(monitor, NullLogger<CliWrapProcessRunner>.Instance);
        var git = new GitRunner(processes, NullLogger<GitRunner>.Instance);
        var engine = new CodingEngine(factory, _registry, prompts, processes, git, monitor, NullLoggerFactory.Instance);
        var workspace = new Workspace(42, _dir.Path, repo, "agent/proj-1-thing", "main", profile ?? RepoProfile.Empty);
        return (engine, workspace);
    }

    private CodingRun Run(Workspace workspace, string instruction = "Add a greeting file.")
        => new(workspace, instruction, null, false, _requester);

    [Fact]
    public async Task RunAsync_WriteFileThenDone_WritesFileAndStopsWithDone()
    {
        var script = new ScriptedChatClient()
            .ThenToolCall("write_file", new Dictionary<string, object?> { ["path"] = "src/hello.txt", ["content"] = "hello" })
            .ThenToolCall("done", new Dictionary<string, object?> { ["summary"] = "Added src/hello.txt." });
        var (engine, workspace) = Build(script);
        var progress = new List<string>();

        var result = await engine.RunAsync(Run(workspace), new Progress<string>(progress.Add), TestContext.Current.CancellationToken);

        Assert.Equal(CodingStopReason.Done, result.StopReason);
        Assert.Equal("Added src/hello.txt.", result.Summary);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(workspace.RepoPath, "src", "hello.txt")));
        Assert.Equal(1, result.Turns);
        Assert.Equal(20, result.TokensUsed);
        Assert.Null(result.Verified);
        Assert.Null(result.Error);
        Assert.Equal(2, script.Calls.Count);

        var system = script.Calls[0][0];
        Assert.Equal(ChatRole.System, system.Role);
        Assert.Contains("Protected paths", system.Text, StringComparison.Ordinal);
        Assert.Contains("agent/proj-1-thing", system.Text, StringComparison.Ordinal);
        var user = script.Calls[0][1];
        Assert.Contains("Add a greeting file.", user.Text, StringComparison.Ordinal);
        Assert.Contains("README.md", user.Text, StringComparison.Ordinal);
        Assert.Contains(script.LastOptions!.Tools!, t => t.Name == "done");
    }

    [Fact]
    public async Task RunAsync_NeverCallsDone_StopsWithBudgetTurns()
    {
        var calls = 0;
        var script = new ScriptedChatClient
        {
            Fallback = (_, _) => ++calls % 2 == 1
                ? new ScriptedChatClient().ToolCall("list_files")
                : ScriptedChatClient.Text("Still looking around."),
        };
        var (engine, workspace) = Build(script, o => o.Budget.MaxTurns = 2);

        var result = await engine.RunAsync(Run(workspace), null, TestContext.Current.CancellationToken);

        Assert.Equal(CodingStopReason.BudgetTurns, result.StopReason);
        Assert.Equal(2, result.Turns);
        Assert.Equal("Still looking around.", result.Summary);
    }

    [Fact]
    public async Task RunAsync_TextOnlyTwice_NudgesOnceThenStops()
    {
        var script = new ScriptedChatClient().ThenText("I think this is done.").ThenText("Yes, done.");
        var (engine, workspace) = Build(script);

        var result = await engine.RunAsync(Run(workspace), null, TestContext.Current.CancellationToken);

        Assert.Equal(CodingStopReason.Done, result.StopReason);
        Assert.Equal(2, result.Turns);
        Assert.Equal("Yes, done.", result.Summary);
        var nudge = script.Calls[1].Last(m => m.Role == ChatRole.User);
        Assert.Equal("Continue. Call done when finished.", nudge.Text);
    }

    [Fact]
    public async Task RunAsync_Cancelled_StopsWithCancelled()
    {
        using var cts = new CancellationTokenSource();
        var script = new ScriptedChatClient()
            .Then((_, _) =>
            {
                cts.Cancel();
                return new ScriptedChatClient().ToolCall("list_files");
            });
        script.Fallback = (_, _) => throw new InvalidOperationException("should not be called after cancellation");
        var (engine, workspace) = Build(script);

        var result = await engine.RunAsync(Run(workspace), null, cts.Token);

        Assert.Equal(CodingStopReason.Cancelled, result.StopReason);
        Assert.Null(result.Verified);
    }

    [Fact]
    public async Task RunAsync_ModelThrows_StopsWithError()
    {
        var script = new ScriptedChatClient().Then((_, _) => throw new HttpRequestException("llm down"));
        var (engine, workspace) = Build(script);

        var result = await engine.RunAsync(Run(workspace), null, TestContext.Current.CancellationToken);

        Assert.Equal(CodingStopReason.Error, result.StopReason);
        Assert.Equal("llm down", result.Error);
    }

    [Fact]
    public async Task RunAsync_ProtectedWrite_IsRefusedAndReportedToModel()
    {
        var script = new ScriptedChatClient()
            .ThenToolCall("write_file", new Dictionary<string, object?> { ["path"] = ".gitlab-ci.yml", ["content"] = "evil" })
            .Then((messages, _) =>
            {
                var toolResult = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last();
                Assert.Contains("protected", toolResult.Result?.ToString(), StringComparison.Ordinal);
                return new ScriptedChatClient().ToolCall("done", new Dictionary<string, object?> { ["summary"] = "refused" });
            });
        var (engine, workspace) = Build(script);

        var result = await engine.RunAsync(Run(workspace), null, TestContext.Current.CancellationToken);

        Assert.Equal(CodingStopReason.Done, result.StopReason);
        Assert.False(File.Exists(Path.Combine(workspace.RepoPath, ".gitlab-ci.yml")));
    }

    [Fact]
    public async Task RunAsync_VerificationFails_FeedsBackAndRetriesThenReportsUnverified()
    {
        GitTestHelper.SkipIfMissing();
        var script = new ScriptedChatClient()
            .ThenToolCall("done", new Dictionary<string, object?> { ["summary"] = "first attempt" })
            .Then((messages, _) =>
            {
                var feedback = messages.Last(m => m.Role == ChatRole.User);
                Assert.StartsWith("Build/test failed:", feedback.Text, StringComparison.Ordinal);
                return new ScriptedChatClient().ToolCall("done", new Dictionary<string, object?> { ["summary"] = "second attempt" });
            });
        // "git nonsense-subcommand" passes policy? No: only allow-listed sub-commands do, so use a real one that fails.
        var profile = RepoProfile.Empty with { BuildCommand = "git cat-file -p 0000000000000000000000000000000000000000" };
        var (engine, workspace) = Build(script, o => o.Budget.MaxVerifyRetries = 1, profile);

        var result = await engine.RunAsync(Run(workspace), null, TestContext.Current.CancellationToken);

        Assert.Equal(CodingStopReason.Done, result.StopReason);
        Assert.False(result.Verified);
        Assert.Equal("second attempt", result.Summary);
        Assert.Equal(2, result.Turns);
        Assert.Contains("$ git cat-file", result.VerificationOutput, StringComparison.Ordinal);
        Assert.Equal(2, result.CommandsRun.Count);

        // The second turn must see a consistent history: system, user, the done call, its result, and the feedback.
        var second = script.Calls[1];
        Assert.Equal(5, second.Count);
        Assert.Equal(1, second.Count(m => m.Contents.OfType<FunctionCallContent>().Any()));
        Assert.Equal(1, second.Count(m => m.Contents.OfType<FunctionResultContent>().Any()));
    }

    [Fact]
    public async Task RunAsync_VerificationPasses_ReportsVerified()
    {
        GitTestHelper.SkipIfMissing();
        var script = new ScriptedChatClient().ThenToolCall("done", new Dictionary<string, object?> { ["summary"] = "ok" });
        var profile = RepoProfile.Empty with { BuildCommand = "git --version", TestCommand = "git --version" };
        var (engine, workspace) = Build(script, profile: profile);

        var result = await engine.RunAsync(Run(workspace), null, TestContext.Current.CancellationToken);

        Assert.True(result.Verified);
        Assert.Equal(2, result.CommandsRun.Count);
    }

    [Fact]
    public async Task RunAsync_ExternalCodingTools_AreOfferedWithoutNameCollisions()
    {
        var external = AIFunctionFactory.Create(() => "docs", "lookup_docs", "Looks up docs");
        var colliding = AIFunctionFactory.Create(() => "x", "read_file", "Collides");
        _registry.GetTools(Arg.Any<CallerIdentity>(), ToolScope.Coding, Channel.GitLab, Arg.Any<IReadOnlyCollection<string>?>())
            .Returns([new ToolDescriptor(external, Role.Team, ToolScope.Coding, "mcp"), new ToolDescriptor(colliding, Role.Team, ToolScope.Coding, "mcp")]);
        var script = new ScriptedChatClient().ThenToolCall("done", new Dictionary<string, object?> { ["summary"] = "ok" });
        var (engine, workspace) = Build(script);

        await engine.RunAsync(Run(workspace), null, TestContext.Current.CancellationToken);

        var names = script.LastOptions!.Tools!.Select(t => t.Name).ToList();
        Assert.Contains("lookup_docs", names);
        Assert.Equal(1, names.Count(n => n == "read_file"));
    }

    [Fact]
    public async Task RunAsync_FollowUp_IncludesPreviousSummary()
    {
        var script = new ScriptedChatClient().ThenToolCall("done", new Dictionary<string, object?> { ["summary"] = "ok" });
        var (engine, workspace) = Build(script);
        var run = new CodingRun(workspace, "Also add tests.", "Added the feature.", true, _requester);

        await engine.RunAsync(run, null, TestContext.Current.CancellationToken);

        var user = script.Calls[0][1].Text;
        Assert.Contains("Follow-up", user, StringComparison.Ordinal);
        Assert.Contains("Added the feature.", user, StringComparison.Ordinal);
        Assert.Contains("Also add tests.", user, StringComparison.Ordinal);
    }
}
