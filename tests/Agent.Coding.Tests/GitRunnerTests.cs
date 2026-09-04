using Microsoft.Extensions.Logging.Abstractions;

namespace Agent.Coding.Tests;

public sealed class GitRunnerTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private GitRunner Create()
    {
        var processes = new CliWrapProcessRunner(TestOptions.Monitor(TestOptions.Coding(_dir.Path)), NullLogger<CliWrapProcessRunner>.Instance);
        return new GitRunner(processes, NullLogger<GitRunner>.Instance);
    }

    [Fact]
    public async Task CloneBranchCommitPush_BranchAppearsInBareRepo()
    {
        GitTestHelper.SkipIfMissing();
        var ct = TestContext.Current.CancellationToken;
        var url = GitTestHelper.CreateBareRepoWithCommit(_dir.Path);
        var git = Create();
        var clone = _dir.Combine("clone");

        await git.CloneAsync(url, clone, null, depth: 0, blobFilter: false, ct);
        Assert.Equal("main", await git.CurrentBranchAsync(clone, ct));
        Assert.False(await git.HasChangesAsync(clone, ct));
        Assert.True(await git.BranchExistsOnRemoteAsync(clone, "main", null, ct));
        Assert.False(await git.BranchExistsOnRemoteAsync(clone, "agent/feature", null, ct));

        await git.CreateBranchAsync(clone, "agent/feature", "origin/main", ct);
        Assert.Equal("agent/feature", await git.CurrentBranchAsync(clone, ct));

        File.WriteAllText(Path.Combine(clone, "hello.txt"), "hi\n");
        File.WriteAllText(Path.Combine(clone, "README.md"), "# Changed\n");
        Assert.True(await git.HasChangesAsync(clone, ct));
        var status = await git.StatusAsync(clone, ct);
        Assert.Equal(["README.md", "hello.txt"], GitRunner.ParseStatus(status).Order(StringComparer.Ordinal));
        Assert.Contains("README.md", await git.DiffAsync(clone, stat: true, ct), StringComparison.Ordinal);

        await git.AddAllAsync(clone, ct);
        await git.CommitAsync(clone, "PROJ-1: add hello\n\nBody with \"quotes\" and $dollars.\n\nRequested-by: Alice\nTask: #1", "Team Agent", "agent@localhost", ct);
        Assert.False(await git.HasChangesAsync(clone, ct));

        await git.PushAsync(clone, "agent/feature", null, setUpstream: true, ct);

        var bare = GitTestHelper.BarePath(_dir.Path);
        var (code, branches) = GitTestHelper.Run(_dir.Path, $"--git-dir={bare}", "branch", "--list", "agent/feature");
        Assert.Equal(0, code);
        Assert.Contains("agent/feature", branches, StringComparison.Ordinal);

        var (_, log) = GitTestHelper.Run(_dir.Path, $"--git-dir={bare}", "log", "-1", "--format=%an <%ae>%n%B", "agent/feature");
        Assert.Contains("Team Agent <agent@localhost>", log, StringComparison.Ordinal);
        Assert.Contains("Body with \"quotes\" and $dollars.", log, StringComparison.Ordinal);
        Assert.Contains("Requested-by: Alice", log, StringComparison.Ordinal);
        Assert.True(await git.BranchExistsOnRemoteAsync(clone, "agent/feature", null, ct));
    }

    [Fact]
    public async Task FetchAndCheckout_ExistingRemoteBranch_Works()
    {
        GitTestHelper.SkipIfMissing();
        var ct = TestContext.Current.CancellationToken;
        var url = GitTestHelper.CreateBareRepoWithCommit(_dir.Path);
        var seed = _dir.Combine("seed");
        GitTestHelper.Run(seed, "checkout", "-q", "-b", "agent/existing");
        File.WriteAllText(Path.Combine(seed, "existing.txt"), "x");
        GitTestHelper.Run(seed, "add", "-A");
        GitTestHelper.Run(seed, "commit", "-q", "-m", "on branch");
        GitTestHelper.Run(seed, "push", "-q", "origin", "agent/existing");

        var git = Create();
        var clone = _dir.Combine("clone");
        await git.CloneAsync(url, clone, null, depth: 1, blobFilter: false, ct);
        await git.FetchAsync(clone, "+refs/heads/agent/existing:refs/remotes/origin/agent/existing", null, depth: 1, ct);
        await git.CheckoutAsync(clone, "agent/existing", ct);

        Assert.Equal("agent/existing", await git.CurrentBranchAsync(clone, ct));
        Assert.True(File.Exists(Path.Combine(clone, "existing.txt")));
    }

    [Fact]
    public async Task Clone_BadUrl_ThrowsGitException()
    {
        GitTestHelper.SkipIfMissing();

        var ex = await Assert.ThrowsAsync<GitException>(() => Create().CloneAsync(GitTestHelper.ToFileUrl(_dir.Combine("missing.git")), _dir.Combine("clone"), null, 0, false, TestContext.Current.CancellationToken));

        Assert.NotEqual(0, ex.ExitCode);
    }

    [Fact]
    public void CredentialEnvironment_UsesConfigVariablesNotCommandLine()
    {
        var env = GitRunner.CredentialEnvironment(("oauth2", "tok"))!;

        Assert.Equal("1", env["GIT_CONFIG_COUNT"]);
        Assert.Equal("http.extraheader", env["GIT_CONFIG_KEY_0"]);
        Assert.Equal("Authorization: Basic " + Convert.ToBase64String("oauth2:tok"u8.ToArray()), env["GIT_CONFIG_VALUE_0"]);
        Assert.Null(GitRunner.CredentialEnvironment(null));
    }

    [Fact]
    public void ParseStatus_HandlesRenamesQuotesAndGarbage()
    {
        const string porcelain = " M src/a.cs\n?? new.txt\nR  old.txt -> new/name.txt\nA  \"with space.txt\"\n…[truncated]…\n";

        var files = GitRunner.ParseStatus(porcelain);

        Assert.Equal(["src/a.cs", "new.txt", "new/name.txt", "with space.txt"], files);
    }
}
