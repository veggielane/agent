using Agent.Coding.Sandbox;

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
        Assert.Null(profile.Container);
    }

    [Fact]
    public void ParseYaml_ContainerBlock_Bound()
    {
        const string yaml = """
            build: npm ci
            container:
              profile: node-chromium
              memory: 6g
              cpus: 4
              network: none
              env:
                PUPPETEER_SKIP_DOWNLOAD: "1"
            """;

        var container = RepoConfigLoader.ParseYaml(yaml).Container;

        Assert.NotNull(container);
        Assert.Equal("node-chromium", container.Profile);
        Assert.Equal("6g", container.Memory);
        Assert.Equal(4, container.Cpus);
        Assert.Equal("none", container.Network);
        Assert.Equal("1", container.Env!["PUPPETEER_SKIP_DOWNLOAD"]);
        Assert.Null(container.Image);
    }

    [Fact]
    public void ParseYaml_EmptyContainerBlock_IsNoRequest()
    {
        Assert.Null(RepoConfigLoader.ParseYaml("container:\nbuild: make\n").Container);
    }

    [Fact]
    public void Load_EngexYml_IsThePreferredFile()
    {
        _dir.Write(".engex.yml", "test: npm test\ncontainer:\n  profile: node\n");
        _dir.Write(".agent/config.yml", "test: legacy\ncontainer:\n  profile: legacy\n");

        var profile = RepoConfigLoader.Load(_dir.Path);

        Assert.Equal("npm test", profile.TestCommand);
        Assert.Equal("node", profile.Container!.Profile);
    }

    [Fact]
    public void Load_EngexYaml_IsAlsoAccepted()
    {
        _dir.Write(".engex.yaml", "container:\n  image: our-registry/x:1\n");

        Assert.Equal("our-registry/x:1", RepoConfigLoader.Load(_dir.Path).Container!.Image);
    }

    [Fact]
    public void Load_LegacyAgentConfig_StillWorks()
    {
        _dir.Write(".agent/config.yml", "container:\n  profile: legacy\n");

        Assert.Equal("legacy", RepoConfigLoader.Load(_dir.Path).Container!.Profile);
    }

    [Fact]
    public void EngexFile_IsProtectedFromTheModel()
    {
        // The model must not be able to rewrite the policy it runs under.
        var guard = new PathGuard(_dir.Path, CodingOptions.DefaultProtectedPaths);

        Assert.True(guard.IsProtected(".engex.yml"));
        Assert.True(guard.IsProtected(".engex.yaml"));
        Assert.True(guard.IsProtected(".agent/config.yml"));
    }
}
