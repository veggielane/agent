using System.Text.Json;
using Agent.Coding.OpenCode;
using Agent.Coding.Sandbox;
using Agent.Core.Authorization;
using Agent.Core.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Agent.Coding.Tests;

public sealed class OpenCodeConfigTests
{
    private static JsonElement Build(Action<CodingOptions>? configure = null, RepoProfile? profile = null)
    {
        var coding = new CodingOptions();
        configure?.Invoke(coding);
        var llm = new LlmOptions { BaseUrl = "https://llm.internal/v1", AnswerModel = "a", CodingModel = "big-coder" };
        var json = OpenCodeConfigWriter.Build(coding.OpenCode, coding, llm, profile ?? RepoProfile.Empty, "big-coder");
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void Config_DeclaresTheTeamEndpointAsAnOpenAiCompatibleProvider()
    {
        var config = Build();

        var provider = config.GetProperty("provider").GetProperty("team");
        Assert.Equal("@ai-sdk/openai-compatible", provider.GetProperty("npm").GetString());
        Assert.Equal("https://llm.internal/v1", provider.GetProperty("options").GetProperty("baseURL").GetString());
        Assert.Equal("{env:OPENCODE_LLM_API_KEY}", provider.GetProperty("options").GetProperty("apiKey").GetString());
        Assert.True(provider.GetProperty("models").TryGetProperty("big-coder", out _));
        Assert.Equal("team/big-coder", config.GetProperty("model").GetString());
        Assert.Equal("AGENTS.md", config.GetProperty("instructions")[0].GetString());
    }

    [Fact]
    public void Permissions_DenyBashByDefaultAndOpenOnlyTheAllowList()
    {
        var config = Build(o => o.AllowedExecutables = ["dotnet", "git"]);

        var bash = config.GetProperty("permission").GetProperty("bash");
        var order = bash.EnumerateObject().Select(p => p.Name).ToList();

        // Later rules win, so the catch-all deny must come first and the git denials last.
        Assert.Equal("*", order[0]);
        Assert.Equal("deny", bash.GetProperty("*").GetString());
        Assert.Equal("allow", bash.GetProperty("dotnet*").GetString());
        Assert.Equal("allow", bash.GetProperty("git*").GetString());
        Assert.Equal("deny", bash.GetProperty("git commit*").GetString());
        Assert.Equal("deny", bash.GetProperty("git push*").GetString());
        Assert.True(order.IndexOf("git*") < order.IndexOf("git commit*"));
    }

    [Fact]
    public void Permissions_DenyEditsToProtectedPaths()
    {
        var config = Build(
            o => o.ProtectedPaths = [".gitlab-ci.yml", "deploy/**"],
            new RepoProfile(null, null, null, [], ["secrets/**"], null));

        var edit = config.GetProperty("permission").GetProperty("edit");

        Assert.Equal("allow", edit.GetProperty("*").GetString());
        Assert.Equal("deny", edit.GetProperty(".gitlab-ci.yml").GetString());
        Assert.Equal("deny", edit.GetProperty("deploy/**").GetString());
        Assert.Equal("deny", edit.GetProperty("secrets/**").GetString());
        Assert.Equal("*", edit.EnumerateObject().First().Name);
    }

    [Fact]
    public void Permissions_CloseTheWebAndInteractiveQuestionsByDefault()
    {
        var config = Build();
        var permission = config.GetProperty("permission");

        Assert.Equal("deny", permission.GetProperty("webfetch").GetString());
        Assert.Equal("deny", permission.GetProperty("websearch").GetString());
        Assert.Equal("deny", permission.GetProperty("external_directory").GetString());

        // A question would block a non-interactive run until the timeout.
        Assert.Equal("deny", permission.GetProperty("question").GetString());
        Assert.Equal("allow", permission.GetProperty("read").GetString());

        var open = Build(o => o.OpenCode.AllowWebAccess = true);
        Assert.Equal("allow", open.GetProperty("permission").GetProperty("webfetch").GetString());
    }

    [Fact]
    public void Permissions_CanBeTurnedOffEntirely()
    {
        var config = Build(o => o.OpenCode.ApplyPermissions = false);

        Assert.False(config.TryGetProperty("permission", out _));
    }

    [Fact]
    public void RunArguments_AreNonInteractiveWithTheMessageLast()
    {
        var options = new OpenCodeOptions { Agent = "build", ExtraArgs = ["--share"] };

        var args = OpenCodeCodingEngine.BuildRunArguments(options, "big-coder", "do the thing", continueSession: false);

        Assert.Equal(["run", "--format", "json", "--auto", "--model", "team/big-coder", "--agent", "build", "--share", "do the thing"], args);
    }

    [Fact]
    public void RunArguments_ContinueAddsTheFlag()
    {
        var args = OpenCodeCodingEngine.BuildRunArguments(new OpenCodeOptions(), "m", "fix it", continueSession: true);

        Assert.Contains("--continue", args);
        Assert.Equal("fix it", args[^1]);
    }

    [Theory]
    [InlineData("{\"type\":\"message\",\"text\":\"Added a cache.\"}", "Added a cache.")]
    [InlineData("{\"parts\":[{\"content\":\"one\"},{\"content\":\"two\"}]}", "one\ntwo")]
    public void Output_ExtractsAssistantText(string line, string expected)
        => Assert.Equal(expected, OpenCodeOutput.Parse(line).Summary);

    [Fact]
    public void Output_SumsTokenCountsWhereverTheyAppear()
    {
        var parsed = OpenCodeOutput.Parse("""
            {"type":"step","usage":{"input":100,"output":20}}
            {"type":"step","tokens":{"input_tokens":5,"completion_tokens":7}}
            """);

        Assert.Equal(105, parsed.InputTokens);
        Assert.Equal(27, parsed.OutputTokens);
        Assert.Equal(132, parsed.TotalTokens);
        Assert.Equal(2, parsed.Messages);
    }

    [Fact]
    public void Output_UnrecognisedShape_FallsBackToRawText()
    {
        // The event schema belongs to opencode; an unfamiliar one must not lose the summary.
        var parsed = OpenCodeOutput.Parse("Done: refactored the parser.\nnot json at all");

        Assert.Contains("refactored the parser", parsed.Summary, StringComparison.Ordinal);
        Assert.Equal(0, parsed.TotalTokens);
        Assert.Equal(0, parsed.Messages);
    }

    [Fact]
    public void Output_Empty_IsHandled()
    {
        var parsed = OpenCodeOutput.Parse("   ");

        Assert.Equal(string.Empty, parsed.Summary);
        Assert.Equal(0, parsed.TotalTokens);
    }

    [Fact]
    public void Message_TellsItNotToCommitAndCarriesTheFollowUpSummary()
    {
        var workspace = new Workspace(1, "root", "repo", "agent/x", "main", RepoProfile.Empty);
        var caller = CallerIdentity.Local("bob", Role.Team);

        var fresh = OpenCodeCodingEngine.BuildMessage(new CodingRun(workspace, "Add caching.", null, false, caller));
        Assert.StartsWith("Add caching.", fresh, StringComparison.Ordinal);
        Assert.Contains("Do not commit, push, or switch branches", fresh, StringComparison.Ordinal);

        var followUp = OpenCodeCodingEngine.BuildMessage(new CodingRun(workspace, "Also add tests.", "Added a cache.", true, caller));
        Assert.Contains("Follow-up", followUp, StringComparison.Ordinal);
        Assert.Contains("Added a cache.", followUp, StringComparison.Ordinal);
    }
}

public sealed class OpenCodeEngineTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly IGitRunner _git = Substitute.For<IGitRunner>();
    private readonly CallerIdentity _requester = CallerIdentity.Local("bob", Role.Team);

    public OpenCodeEngineTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir.Path, "repo"));
        _git.StatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(" M src/Cache.cs\n");
    }

    public void Dispose() => _dir.Dispose();

    private string RepoPath => Path.Combine(_dir.Path, "repo");

    private (OpenCodeCodingEngine Engine, FakeSandbox Sandbox, Workspace Workspace) Build(
        Action<CodingOptions>? configure = null,
        RepoProfile? profile = null,
        Func<ParsedCommand, ProcessResult>? handler = null)
    {
        var options = TestOptions.Coding(_dir.Path, o =>
        {
            o.Engine = CodingEngineKind.OpenCode;
            configure?.Invoke(o);
        });

        var sandbox = new FakeSandbox();
        var monitor = TestOptions.Monitor(options);
        var llm = new TestOptionsMonitor<LlmOptions>(new LlmOptions { BaseUrl = "https://llm.internal/v1", AnswerModel = "a", CodingModel = "big-coder", ApiKey = "sk-secret" });

        var clients = Substitute.For<IChatClientFactory>();
        clients.ResolveModel(ModelPurpose.Coding, Arg.Any<string?>(), Arg.Any<string?>()).Returns("big-coder");

        var processes = new RecordingProcessRunner();
        var engine = new OpenCodeCodingEngine(sandbox, _git, processes, monitor, llm, clients, NullLoggerFactory.Instance);
        var workspace = new Workspace(9, _dir.Path, RepoPath, "agent/x", "main", profile ?? RepoProfile.Empty);

        sandbox.Handler = handler;
        return (engine, sandbox, workspace);
    }

    private CodingRun Run(Workspace workspace) => new(workspace, "Add caching.", null, false, _requester);

    /// <summary>Version check succeeds, the run reports a summary.</summary>
    private static ProcessResult Script(ParsedCommand command)
        => command.Arguments.Contains("--version")
            ? new ProcessResult(0, "opencode 1.2.3", string.Empty, false)
            : new ProcessResult(0, "{\"type\":\"message\",\"text\":\"Added a cache.\"}\n{\"usage\":{\"input\":10,\"output\":5}}", string.Empty, false);

    [Fact]
    public async Task Run_WritesConfigRunsOpenCodeAndReportsTheSummary()
    {
        var (engine, sandbox, workspace) = Build(handler: Script);

        var result = await engine.RunAsync(Run(workspace), null, TestContext.Current.CancellationToken);

        Assert.Equal(CodingStopReason.Done, result.StopReason);
        Assert.Equal("Added a cache.", result.Summary);
        Assert.Equal(15, result.TokensUsed);
        Assert.Equal(["src/Cache.cs"], result.ChangedFiles);

        var session = sandbox.Session!;
        Assert.StartsWith("opencode --version", session.Commands[0], StringComparison.Ordinal);
        Assert.Contains("run --format json --auto --model team/big-coder", session.Commands[1], StringComparison.Ordinal);

        var environment = session.Environments[1]!;
        Assert.Equal("/work/opencode.agent.json", environment["OPENCODE_CONFIG"]);
        Assert.Equal("sk-secret", environment["OPENCODE_LLM_API_KEY"]);
        Assert.True(sandbox.Session.Disposed);
    }

    [Fact]
    public async Task Run_ExcludesTheGeneratedConfigFromGitAndDeletesIt()
    {
        var (engine, _, workspace) = Build(handler: Script);

        await engine.RunAsync(Run(workspace), null, TestContext.Current.CancellationToken);

        // It must never reach a commit, and it must not survive the run.
        Assert.Contains("/opencode.agent.json", File.ReadAllText(Path.Combine(RepoPath, ".git", "info", "exclude")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(RepoPath, "opencode.agent.json")));
    }

    [Fact]
    public async Task Run_OpenCodeMissing_FailsWithAnActionableMessage()
    {
        var (engine, sandbox, workspace) = Build(handler: _ => new ProcessResult(127, string.Empty, "command not found", false));

        var result = await engine.RunAsync(Run(workspace), null, TestContext.Current.CancellationToken);

        Assert.Equal(CodingStopReason.Error, result.StopReason);
        Assert.Contains("not available", result.Summary, StringComparison.Ordinal);
        Assert.Contains("Coding:Engine to Native", result.Error, StringComparison.Ordinal);
        Assert.Single(sandbox.Session!.Commands);
    }

    [Fact]
    public async Task Run_TimeoutIsReportedAsABudgetStop()
    {
        var (engine, _, workspace) = Build(handler: command => command.Arguments.Contains("--version")
            ? new ProcessResult(0, "1.2.3", string.Empty, false)
            : new ProcessResult(-1, "partial", string.Empty, TimedOut: true));

        var result = await engine.RunAsync(Run(workspace), null, TestContext.Current.CancellationToken);

        Assert.Equal(CodingStopReason.BudgetTime, result.StopReason);
    }

    [Fact]
    public async Task Run_NonZeroExit_IsAnErrorWithTheOutput()
    {
        var (engine, _, workspace) = Build(handler: command => command.Arguments.Contains("--version")
            ? new ProcessResult(0, "1.2.3", string.Empty, false)
            : new ProcessResult(2, string.Empty, "provider rejected the request", false));

        var result = await engine.RunAsync(Run(workspace), null, TestContext.Current.CancellationToken);

        Assert.Equal(CodingStopReason.Error, result.StopReason);
        Assert.Contains("provider rejected", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_ProtectedPathModified_FailsAndIsNotPublished()
    {
        // opencode's generated permissions should stop this; the post-run check makes it a guarantee.
        _git.StatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(" M .gitlab-ci.yml\n M src/Cache.cs\n");
        var (engine, _, workspace) = Build(handler: Script);

        var result = await engine.RunAsync(Run(workspace), null, TestContext.Current.CancellationToken);

        Assert.Equal(CodingStopReason.Error, result.StopReason);
        Assert.Contains(".gitlab-ci.yml", result.Summary, StringComparison.Ordinal);
        Assert.Contains("not published", result.Error, StringComparison.Ordinal);
        Assert.False(result.Verified);
    }

    [Fact]
    public async Task Run_VerificationFailure_FeedsBackThroughContinue()
    {
        var attempts = 0;
        var (engine, sandbox, workspace) = Build(
            profile: new RepoProfile("dotnet build", null, null, [], [], null),
            handler: command =>
            {
                if (command.Arguments.Contains("--version"))
                {
                    return new ProcessResult(0, "1.2.3", string.Empty, false);
                }

                if (command.Executable == "dotnet")
                {
                    // Fails once, then passes after the fix-up turn.
                    return new ProcessResult(attempts++ == 0 ? 1 : 0, "build output", string.Empty, false);
                }

                return new ProcessResult(0, "{\"text\":\"fixed\"}", string.Empty, false);
            });

        var result = await engine.RunAsync(Run(workspace), null, TestContext.Current.CancellationToken);

        Assert.True(result.Verified);
        Assert.Equal(2, result.Turns);
        Assert.Contains(sandbox.Session!.Commands, c => c.Contains("--continue", StringComparison.Ordinal));
        Assert.Equal(CodingStopReason.Done, result.StopReason);
    }

    [Fact]
    public async Task Run_SandboxCannotStart_Fails()
    {
        var (engine, _, workspace) = Build();
        var failing = new OpenCodeCodingEngine(
            new FakeSandbox(new SandboxException("no docker daemon")),
            _git,
            new RecordingProcessRunner(),
            TestOptions.Monitor(TestOptions.Coding(_dir.Path, o => o.Engine = CodingEngineKind.OpenCode)),
            new TestOptionsMonitor<LlmOptions>(new LlmOptions { BaseUrl = "u", AnswerModel = "a" }),
            Substitute.For<IChatClientFactory>(),
            NullLoggerFactory.Instance);

        var result = await failing.RunAsync(Run(workspace), null, TestContext.Current.CancellationToken);

        Assert.Equal(CodingStopReason.Error, result.StopReason);
        Assert.Contains("no docker daemon", result.Error, StringComparison.Ordinal);
    }
}
