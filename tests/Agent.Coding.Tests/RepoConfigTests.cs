namespace Agent.Coding.Tests;

public sealed class RepoConfigTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void ParseYaml_AllKeys_Bound()
    {
        const string yaml = """
            build: dotnet build -warnaserror
            test: dotnet test --no-build
            lint: dotnet format --verify-no-changes
            allowedExecutables: [gradle, "java"]
            protectedPaths:
              - "**/*.snk"
              - infra/**
            instructions: |
              Use file-scoped namespaces.
              Run the tests.
            unknownKey: ignored
            """;

        var profile = RepoConfigLoader.ParseYaml(yaml);

        Assert.Equal("dotnet build -warnaserror", profile.BuildCommand);
        Assert.Equal("dotnet test --no-build", profile.TestCommand);
        Assert.Equal("dotnet format --verify-no-changes", profile.LintCommand);
        Assert.Equal(["gradle", "java"], profile.ExtraAllowedExecutables);
        Assert.Equal(["**/*.snk", "infra/**"], profile.ExtraProtectedPaths);
        Assert.Contains("file-scoped", profile.Instructions, StringComparison.Ordinal);
        Assert.True(profile.HasVerification);
    }

    [Theory]
    [InlineData("")]
    [InlineData("build: [unclosed")]
    public void ParseYaml_EmptyOrInvalid_ReturnsEmptyProfile(string yaml)
    {
        var profile = RepoConfigLoader.ParseYaml(yaml);

        Assert.Equal(RepoProfile.Empty, profile);
    }

    [Fact]
    public void Detect_Solution_UsesDotnet()
    {
        _dir.Write("App.sln", string.Empty);

        var profile = RepoConfigLoader.Detect(_dir.Path);

        Assert.Equal("dotnet build", profile.BuildCommand);
        Assert.Equal("dotnet test --no-build", profile.TestCommand);
    }

    [Fact]
    public void Detect_PackageJson_UsesNpm()
    {
        _dir.Write("package.json", "{}");

        var profile = RepoConfigLoader.Detect(_dir.Path);

        Assert.Equal("npm ci", profile.BuildCommand);
        Assert.Equal("npm test", profile.TestCommand);
    }

    [Fact]
    public void Detect_Makefile_UsesMake()
    {
        _dir.Write("Makefile", "all:\n");

        var profile = RepoConfigLoader.Detect(_dir.Path);

        Assert.Equal("make", profile.BuildCommand);
        Assert.Equal("make test", profile.TestCommand);
    }

    [Fact]
    public void Detect_Pyproject_UsesPytest()
    {
        _dir.Write("pyproject.toml", "[project]\n");

        var profile = RepoConfigLoader.Detect(_dir.Path);

        Assert.Null(profile.BuildCommand);
        Assert.Equal("pytest", profile.TestCommand);
    }

    [Fact]
    public void Detect_Nothing_NoVerification()
    {
        var profile = RepoConfigLoader.Detect(_dir.Path);

        Assert.False(profile.HasVerification);
    }

    [Fact]
    public void Load_ConfigOverridesDetection_AndMergesAgentsMd()
    {
        _dir.Write("App.csproj", "<Project />");
        _dir.Write(".agent/config.yml", "test: dotnet test --filter Unit\ninstructions: Keep it small.\n");
        _dir.Write("AGENTS.md", "# Guidance\nRun the linter.\n");

        var profile = RepoConfigLoader.Load(_dir.Path);

        Assert.Equal("dotnet build", profile.BuildCommand);
        Assert.Equal("dotnet test --filter Unit", profile.TestCommand);
        Assert.StartsWith("# Guidance", profile.Instructions, StringComparison.Ordinal);
        Assert.EndsWith("Keep it small.", profile.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_NoFiles_ReturnsDetectedOnly()
    {
        var profile = RepoConfigLoader.Load(_dir.Path);

        Assert.Null(profile.Instructions);
        Assert.Empty(profile.ExtraAllowedExecutables);
    }
}
