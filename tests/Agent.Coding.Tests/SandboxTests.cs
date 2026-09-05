using Agent.Coding.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent.Coding.Tests;

/// <summary>An <see cref="IProcessRunner"/> that records invocations and returns scripted results.</summary>
public sealed class RecordingProcessRunner : IProcessRunner
{
    private readonly Queue<ProcessResult> _results = new();

    public List<(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, TimeSpan Timeout)> Calls { get; } = [];

    public ProcessResult Default { get; set; } = new(0, string.Empty, string.Empty, false);

    public RecordingProcessRunner Returns(ProcessResult result)
    {
        _results.Enqueue(result);
        return this;
    }

    public RecordingProcessRunner Returns(int exitCode, string stdout = "", string stderr = "")
        => Returns(new ProcessResult(exitCode, stdout, stderr, false));

    public (string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, TimeSpan Timeout) Call(int index) => Calls[index];

    public string ArgLine(int index) => string.Join(' ', Calls[index].Arguments);

    public Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? standardInput = null)
    {
        Calls.Add((executable, arguments.ToList(), workingDirectory, timeout));
        return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : Default);
    }
}

/// <summary>A sandbox that records what it was asked to run, for engine-level tests.</summary>
public sealed class FakeSandbox : ISandbox
{
    private readonly Exception? _startFailure;

    public FakeSandbox(Exception? startFailure = null) => _startFailure = startFailure;

    public string Name => "fake";

    public FakeSandboxSession? Session { get; private set; }

    public Task<ISandboxSession> StartAsync(Workspace workspace, CancellationToken cancellationToken)
    {
        if (_startFailure is not null)
        {
            throw _startFailure;
        }

        Session = new FakeSandboxSession();
        return Task.FromResult<ISandboxSession>(Session);
    }
}

public sealed class FakeSandboxSession : ISandboxSession
{
    public List<string> Commands { get; } = [];

    public bool Disposed { get; private set; }

    public ProcessResult Result { get; set; } = new(0, "sandbox output", string.Empty, false);

    public string Description => "a fake container";

    public string Mode => "fake";

    public Task<ProcessResult> ExecuteAsync(ParsedCommand command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Commands.Add(command.ToString());
        return Task.FromResult(Result);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

public sealed class SandboxTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private Workspace Workspace(RepoProfile? profile = null)
    {
        var repo = _dir.Combine("repo");
        Directory.CreateDirectory(repo);
        return new Workspace(7, _dir.Path, repo, "agent/x", "main", profile ?? RepoProfile.Empty);
    }

    private static DockerSandbox Docker(IProcessRunner processes, Action<SandboxOptions>? configure = null)
    {
        var options = new CodingOptions { Sandbox = { Mode = SandboxMode.Docker } };
        configure?.Invoke(options.Sandbox);
        return new DockerSandbox(processes, TestOptions.Monitor(options), NullLogger<DockerSandbox>.Instance);
    }

    [Fact]
    public void BuildRunArguments_AppliesIsolationLimitsAndMount()
    {
        var options = new SandboxOptions { Network = "none", Memory = "2g", Cpus = 1.5, PidsLimit = 128, User = "1000:1000", WorkDir = "/src", DefaultImage = "img:1" };
        var workspace = Workspace();

        var args = DockerSandbox.BuildRunArguments(options, workspace, SandboxPolicy.Resolve(options, workspace.Profile), "agent-task-7-abc");
        var line = string.Join(' ', args);

        Assert.Equal("run", args[0]);
        Assert.Contains("--detach", args);
        Assert.Contains("--name agent-task-7-abc", line, StringComparison.Ordinal);
        Assert.Contains("--label agent.task=7", line, StringComparison.Ordinal);
        Assert.Contains("--network none", line, StringComparison.Ordinal);
        Assert.Contains("--memory 2g", line, StringComparison.Ordinal);
        Assert.Contains("--cpus 1.5", line, StringComparison.Ordinal);
        Assert.Contains("--pids-limit 128", line, StringComparison.Ordinal);
        Assert.Contains("--security-opt no-new-privileges", line, StringComparison.Ordinal);
        Assert.Contains("--cap-drop ALL", line, StringComparison.Ordinal);
        Assert.Contains("--user 1000:1000", line, StringComparison.Ordinal);
        Assert.Contains("--init", args);
        Assert.Contains("--tmpfs", args);
        Assert.Contains($"{Path.GetFullPath(workspace.RepoPath)}:/src", args);
        Assert.Contains("--workdir /src", line, StringComparison.Ordinal);

        // The image and the idle command come last, in that order.
        Assert.Equal(["img:1", "sleep", "infinity"], args.TakeLast(3));
    }

    [Fact]
    public void BuildRunArguments_AddsVolumesEnvAndExtraArgs()
    {
        var options = new SandboxOptions
        {
            Volumes = ["agent-nuget:/root/.nuget/packages", "  "],
            Env = { ["HTTP_PROXY"] = "http://proxy:3128" },
            ExtraArgs = ["--dns", "10.0.0.1"],
            ReadOnlyRootFilesystem = true,
            Init = false,
            TmpfsSize = string.Empty,
        };

        var workspace = Workspace();
        var args = DockerSandbox.BuildRunArguments(options, workspace, SandboxPolicy.Resolve(options, workspace.Profile), "c1");
        var line = string.Join(' ', args);

        Assert.Contains("agent-nuget:/root/.nuget/packages", args);
        Assert.Contains("HTTP_PROXY=http://proxy:3128", args);
        Assert.Contains("--dns 10.0.0.1", line, StringComparison.Ordinal);
        Assert.Contains("--read-only", args);
        Assert.DoesNotContain("--init", args);
        Assert.DoesNotContain("--tmpfs", args);
        Assert.DoesNotContain("  ", args);
    }

    [Fact]
    public void BuildEnvironment_ForcesQuietToolingAndSafeGitOwnership()
    {
        var env = DockerSandbox.BuildEnvironment(null);

        Assert.Equal("1", env["CI"]);
        Assert.Equal("0", env["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("1", env["DOTNET_NOLOGO"]);
        // The bind mount is owned by the host user; without this git refuses to read the repository.
        Assert.Equal("safe.directory", env["GIT_CONFIG_KEY_0"]);
        Assert.Equal("*", env["GIT_CONFIG_VALUE_0"]);
    }

    [Fact]
    public void BuildExecArguments_WrapsInTimeoutAndKeepsArgumentsSeparate()
    {
        var options = new SandboxOptions { WorkDir = "/work" };
        var command = new ParsedCommand("dotnet", ["test", "--filter", "Name~A B"]);

        var args = DockerSandbox.BuildExecArguments(options, "c1", command, TimeSpan.FromSeconds(90));

        Assert.Equal(["exec", "--workdir", "/work", "c1", "timeout", "-s", "KILL", "90", "dotnet", "test", "--filter", "Name~A B"], args);
    }

    [Fact]
    public void BuildExecArguments_WithoutWrapper_RunsCommandDirectly()
    {
        var args = DockerSandbox.BuildExecArguments(new SandboxOptions { UseTimeoutWrapper = false }, "c1", new ParsedCommand("ls", []), TimeSpan.FromSeconds(30));

        Assert.Equal(["exec", "--workdir", "/work", "c1", "ls"], args);
    }

    [Theory]
    [InlineData("dotnet build", "mcr.microsoft.com/dotnet/sdk:10.0")]
    [InlineData("npm test", "node:22")]
    [InlineData("pytest -q", "python:3.12")]
    [InlineData("go test ./...", "golang:1.23")]
    [InlineData("cargo test", "rust:1")]
    [InlineData("make all", "mcr.microsoft.com/dotnet/sdk:10.0")]
    public void ResolveImage_PicksImageForTheRepositoryToolchain(string buildCommand, string expected)
    {
        var profile = new RepoProfile(buildCommand, null, null, [], [], null);

        Assert.Equal(expected, DockerSandbox.ResolveImage(new SandboxOptions(), profile));
    }

    [Fact]
    public void ResolveImage_ConfiguredImageWins_AndUnknownToolchainFallsBack()
    {
        var options = new SandboxOptions { DefaultImage = "base:1" };
        options.Images["node"] = "our-registry/node:22";

        Assert.Equal("our-registry/node:22", DockerSandbox.ResolveImage(options, new RepoProfile("npm ci", null, null, [], [], null)));
        Assert.Equal("base:1", DockerSandbox.ResolveImage(options, RepoProfile.Empty));
    }

    [Fact]
    public void ToolchainOf_UsesTestOrLintWhenBuildIsAbsent()
    {
        Assert.Equal("python", DockerSandbox.ToolchainOf(new RepoProfile(null, "pytest", null, [], [], null)));
        Assert.Equal("node", DockerSandbox.ToolchainOf(new RepoProfile(null, null, "npx eslint .", [], [], null)));
        Assert.Null(DockerSandbox.ToolchainOf(RepoProfile.Empty));
    }

    [Fact]
    public async Task StartAsync_StartsContainerThenExecutesInIt_AndRemovesItOnDispose()
    {
        var processes = new RecordingProcessRunner().Returns(0, "container-id\n").Returns(0, "ok");
        var sandbox = Docker(processes);
        var workspace = Workspace(new RepoProfile("dotnet build", null, null, [], [], null));

        var session = await sandbox.StartAsync(workspace, TestContext.Current.CancellationToken);
        var containerName = processes.Call(0).Arguments[processes.Call(0).Arguments.ToList().IndexOf("--name") + 1];

        Assert.StartsWith("agent-task-7-", containerName, StringComparison.Ordinal);
        Assert.Equal("docker", processes.Call(0).Executable);
        Assert.Contains("mcr.microsoft.com/dotnet/sdk:10.0", processes.Call(0).Arguments);
        Assert.Contains("container", session.Description, StringComparison.OrdinalIgnoreCase);

        var result = await session.ExecuteAsync(new ParsedCommand("dotnet", ["build"]), TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("ok", result.StdOut);
        Assert.Equal(["exec", "--workdir", "/work", containerName, "timeout", "-s", "KILL", "60", "dotnet", "build"], processes.Call(1).Arguments);
        // The host wait is longer than the in-container timeout so the container kills the process first.
        Assert.True(processes.Call(1).Timeout > TimeSpan.FromSeconds(60));

        await session.DisposeAsync();

        Assert.Equal(["rm", "--force", containerName], processes.Call(2).Arguments);
    }

    [Fact]
    public async Task StartAsync_RepositoryProfile_ReachesTheDockerCommand()
    {
        var processes = new RecordingProcessRunner().Returns(0, "id");
        var sandbox = Docker(processes, o =>
        {
            o.Profiles["node-chromium"] = new SandboxProfile { Image = "our-registry/node-chromium:22", Memory = "6g", Network = "none" };
            o.MaxMemory = "8g";
        });
        var repo = new RepoProfile("npm ci", null, null, [], [], null, new RepoContainer(Profile: "node-chromium", Cpus: 3));

        var session = await sandbox.StartAsync(Workspace(repo), TestContext.Current.CancellationToken);
        var line = string.Join(' ', processes.Call(0).Arguments);

        Assert.Contains("our-registry/node-chromium:22", line, StringComparison.Ordinal);
        Assert.Contains("--memory 6g", line, StringComparison.Ordinal);
        Assert.Contains("--network none", line, StringComparison.Ordinal);
        Assert.Contains("--cpus 3", line, StringComparison.Ordinal);
        Assert.Contains("no network access", session.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_RepositoryAsksForADisallowedImage_FailsWithoutStartingAnything()
    {
        var processes = new RecordingProcessRunner();
        var repo = new RepoProfile(null, null, null, [], [], null, new RepoContainer(Image: "docker.io/evil:latest"));

        var ex = await Assert.ThrowsAsync<SandboxException>(() => Docker(processes).StartAsync(Workspace(repo), TestContext.Current.CancellationToken));

        Assert.Contains("does not allow repositories to name images", ex.Message, StringComparison.Ordinal);
        Assert.Empty(processes.Calls);
    }

    [Fact]
    public async Task StartAsync_DockerFails_ThrowsSandboxExceptionWithOutput()
    {
        var processes = new RecordingProcessRunner().Returns(125, string.Empty, "no such image: img");

        var ex = await Assert.ThrowsAsync<SandboxException>(() => Docker(processes).StartAsync(Workspace(), TestContext.Current.CancellationToken));

        Assert.Contains("no such image", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_DockerTimesOut_NamesBothLikelyCauses()
    {
        var processes = new RecordingProcessRunner().Returns(new ProcessResult(-1, string.Empty, "killed", true));
        var workspace = Workspace();

        var ex = await Assert.ThrowsAsync<SandboxException>(() => Docker(processes, o => o.StartTimeoutSeconds = 30).StartAsync(workspace, TestContext.Current.CancellationToken));

        Assert.Contains("image pull", ex.Message, StringComparison.Ordinal);
        // An unshared workspace root makes docker run hang instead of failing, so name it here.
        Assert.Contains(Path.GetFullPath(workspace.RepoPath), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_ContainerTimeoutExitCode_IsReportedAsTimedOut()
    {
        var processes = new RecordingProcessRunner()
            .Returns(0, "id")
            .Returns(DockerSandbox.KilledByTimeoutExitCode, string.Empty, "partial output");
        var session = await Docker(processes).StartAsync(Workspace(), TestContext.Current.CancellationToken);

        var result = await session.ExecuteAsync(new ParsedCommand("sleep", ["600"]), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        Assert.False(result.Success);
        Assert.Contains("killed after 5s inside the container", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_AfterDispose_Throws()
    {
        var processes = new RecordingProcessRunner().Returns(0, "id");
        var session = await Docker(processes).StartAsync(Workspace(), TestContext.Current.CancellationToken);
        await session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.ExecuteAsync(new ParsedCommand("ls", []), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent_AndRemovesOnce()
    {
        var processes = new RecordingProcessRunner().Returns(0, "id");
        var session = await Docker(processes).StartAsync(Workspace(), TestContext.Current.CancellationToken);

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(2, processes.Calls.Count);
    }

    [Fact]
    public async Task ProcessSandbox_RunsOnTheHostInTheRepositoryDirectory()
    {
        var processes = new RecordingProcessRunner().Returns(0, "hi");
        var workspace = Workspace();

        await using var session = await new ProcessSandbox(processes).StartAsync(workspace, TestContext.Current.CancellationToken);
        var result = await session.ExecuteAsync(new ParsedCommand("echo", ["hi"]), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("hi", result.StdOut);
        Assert.Equal("echo", processes.Call(0).Executable);
        Assert.Equal(workspace.RepoPath, processes.Call(0).WorkingDirectory);
        Assert.Contains("host process", session.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Selector_FollowsTheConfiguredMode()
    {
        var options = new CodingOptions();
        var monitor = TestOptions.Monitor(options);
        var processes = new RecordingProcessRunner().Returns(0, "id");
        var selector = new SandboxSelector(monitor, new ProcessSandbox(processes), new DockerSandbox(processes, monitor, NullLogger<DockerSandbox>.Instance));

        Assert.Equal("process", selector.Name);
        var host = await selector.StartAsync(Workspace(), TestContext.Current.CancellationToken);
        Assert.IsType<ProcessSandboxSession>(host);
        Assert.Empty(processes.Calls);

        options.Sandbox.Mode = SandboxMode.Docker;

        Assert.Equal("docker", selector.Name);
        var container = await selector.StartAsync(Workspace(), TestContext.Current.CancellationToken);
        Assert.IsType<DockerSandboxSession>(container);
        Assert.Equal("run", processes.Call(0).Arguments[0]);
    }

    [Fact]
    public async Task Status_ReportsDaemonVersionOrWhyItIsUnavailable()
    {
        var running = new RecordingProcessRunner().Returns(0, "27.1.1\n");
        Assert.Contains("docker 27.1.1", await Docker(running).GetStatusAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        var down = new RecordingProcessRunner().Returns(1, string.Empty, "Cannot connect to the Docker daemon\nmore");
        Assert.Contains("Cannot connect to the Docker daemon", await Docker(down).GetStatusAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        var off = new RecordingProcessRunner();
        Assert.Contains("not in use", await new DockerSandbox(off, TestOptions.Monitor(new CodingOptions()), NullLogger<DockerSandbox>.Instance).GetStatusAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Empty(off.Calls);
    }
}
