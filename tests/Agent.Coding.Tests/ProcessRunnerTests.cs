using Microsoft.Extensions.Logging.Abstractions;

namespace Agent.Coding.Tests;

public sealed class ProcessRunnerTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private CliWrapProcessRunner Runner(int maxOutput = 12_000)
        => new(TestOptions.Monitor(TestOptions.Coding(_dir.Path, o => o.MaxToolOutputChars = maxOutput)), NullLogger<CliWrapProcessRunner>.Instance);

    [Fact]
    public async Task RunAsync_GitVersion_CapturesStdout()
    {
        GitTestHelper.SkipIfMissing();

        var result = await Runner().RunAsync("git", ["--version"], _dir.Path, null, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("git version", result.StdOut, StringComparison.Ordinal);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_MissingExecutable_ReturnsFailureInsteadOfThrowing()
    {
        var result = await Runner().RunAsync("definitely-not-an-executable-xyz", [], _dir.Path, null, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("could not start", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_Environment_IsScrubbedAndForcedValuesSet()
    {
        Environment.SetEnvironmentVariable("AGENT_TEST_SECRET", "s3cret-value");
        try
        {
            var (exe, args) = OperatingSystem.IsWindows()
                ? ("cmd", new[] { "/c", "set" })
                : ("env", Array.Empty<string>());

            var result = await Runner(100_000).RunAsync(exe, args, _dir.Path, new Dictionary<string, string> { ["AGENT_EXTRA"] = "yes" }, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.CombinedOutput);
            Assert.DoesNotContain("s3cret-value", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("CI=1", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("GIT_TERMINAL_PROMPT=0", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("AGENT_EXTRA=yes", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("PATH=", result.StdOut, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_TEST_SECRET", null);
        }
    }

    [Fact]
    public void BuildEnvironment_DropsUnknownVariablesAndKeepsAllowList()
    {
        Environment.SetEnvironmentVariable("AGENT_TEST_DROP_ME", "x");
        try
        {
            var env = CliWrapProcessRunner.BuildEnvironment(null);

            Assert.True(env.ContainsKey("AGENT_TEST_DROP_ME"));
            Assert.Null(env["AGENT_TEST_DROP_ME"]);
            Assert.Equal("1", env["DOTNET_NOLOGO"]);
            Assert.Equal("0", env["GIT_TERMINAL_PROMPT"]);
            if (Environment.GetEnvironmentVariable("PATH") is { } path)
            {
                Assert.Equal(path, env["PATH"]);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_TEST_DROP_ME", null);
        }
    }

    [Fact]
    public void TruncateMiddle_KeepsHeadAndTail()
    {
        var text = new string('a', 500) + new string('b', 500);

        var cut = TextUtil.TruncateMiddle(text, 200);

        Assert.True(cut.Length <= 200 + 5);
        Assert.StartsWith("aaaa", cut, StringComparison.Ordinal);
        Assert.EndsWith("bbbb", cut, StringComparison.Ordinal);
        Assert.Contains("truncated", cut, StringComparison.Ordinal);
    }
}
