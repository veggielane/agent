namespace Agent.Coding.Tests;

public sealed class PathGuardTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("src/Program.cs")]
    [InlineData("./src/../README.md")]
    [InlineData("a/b/c.txt")]
    public void Resolve_PathInsideWorkspace_ReturnsFullPath(string relative)
    {
        var guard = new PathGuard(_dir.Path);

        var full = guard.Resolve(relative);

        Assert.StartsWith(guard.Root, full, StringComparison.OrdinalIgnoreCase);
        Assert.True(Path.IsPathFullyQualified(full));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData("..")]
    public void Resolve_Escape_Throws(string relative)
    {
        var guard = new PathGuard(_dir.Path);

        var ex = Assert.Throws<CodingPolicyException>(() => guard.Resolve(relative));

        Assert.Contains("outside", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_AbsolutePathOutside_Throws()
    {
        var guard = new PathGuard(_dir.Path);
        var outside = Path.Combine(Path.GetTempPath(), "somewhere-else.txt");

        Assert.Throws<CodingPolicyException>(() => guard.Resolve(outside));
    }

    [Fact]
    public void Resolve_AbsolutePathInside_Allowed()
    {
        var guard = new PathGuard(_dir.Path);
        var inside = Path.Combine(_dir.Path, "inside.txt");

        Assert.Equal(inside, guard.Resolve(inside));
    }

    [Fact]
    public void Resolve_SiblingWithSamePrefix_Throws()
    {
        // "/root" must not accept "/root-other/file".
        var root = _dir.Combine("ws");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(_dir.Combine("ws-other"));
        var guard = new PathGuard(root);

        Assert.Throws<CodingPolicyException>(() => guard.Resolve(_dir.Combine("ws-other", "file.txt")));
    }

    [Fact]
    public void Resolve_EmptyOrDot_ReturnsRoot()
    {
        var guard = new PathGuard(_dir.Path);

        Assert.Equal(guard.Root, guard.Resolve(string.Empty));
        Assert.Equal(guard.Root, guard.Resolve("."));
        Assert.Equal(guard.Root, guard.Resolve(null));
    }

    [Theory]
    [InlineData(".gitlab-ci.yml", true)]
    [InlineData(".github/workflows/ci.yml", true)]
    [InlineData("certs/server.pfx", true)]
    [InlineData("server.pfx", true)]
    [InlineData("src/App/appsettings.Production.json", true)]
    [InlineData("deploy/k8s/app.yaml", true)]
    [InlineData(".agent/config.yml", true)]
    [InlineData(".git/hooks/pre-commit", true)]
    [InlineData("src/.git/config", true)]
    [InlineData("src/Program.cs", false)]
    [InlineData("appsettings.json", false)]
    [InlineData("docs/deploy.md", false)]
    [InlineData("README.md", false)]
    public void IsProtected_DefaultGlobs(string relative, bool expected)
    {
        var guard = new PathGuard(_dir.Path, CodingOptions.DefaultProtectedPaths);

        Assert.Equal(expected, guard.IsProtected(relative));
    }

    [Fact]
    public void IsProtected_EscapingPath_CountsAsProtected()
    {
        var guard = new PathGuard(_dir.Path, CodingOptions.DefaultProtectedPaths);

        Assert.True(guard.IsProtected("../x"));
        Assert.True(guard.IsProtected(string.Empty));
    }

    [Fact]
    public void IsProtected_PerRepoGlobs_AreAdded()
    {
        var guard = new PathGuard(_dir.Path, ["secrets/**", "*.key"]);

        Assert.True(guard.IsProtected("secrets/db.txt"));
        Assert.True(guard.IsProtected("id.key"));
        Assert.False(guard.IsProtected("src/id.cs"));
    }

    [Fact]
    public void ResolveForWrite_ProtectedPath_Throws()
    {
        var guard = new PathGuard(_dir.Path, CodingOptions.DefaultProtectedPaths);

        var ex = Assert.Throws<CodingPolicyException>(() => guard.ResolveForWrite(".gitlab-ci.yml"));

        Assert.Contains("protected", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveForWrite_Root_Throws()
    {
        var guard = new PathGuard(_dir.Path);

        Assert.Throws<CodingPolicyException>(() => guard.ResolveForWrite("."));
    }

    [Fact]
    public void ToRelative_UsesForwardSlashes()
    {
        var guard = new PathGuard(_dir.Path);

        Assert.Equal("a/b/c.txt", guard.ToRelative(Path.Combine(_dir.Path, "a", "b", "c.txt")));
        Assert.Equal(string.Empty, guard.ToRelative(_dir.Path));
    }
}
