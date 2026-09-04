using Agent.Coding.Sandbox;

namespace Agent.Coding.Tests;

/// <summary>
/// The rules a repository's <c>.engex.yml</c> container block is held to. Repository content is untrusted,
/// so every case here is about narrowing being allowed and widening being refused.
/// </summary>
public sealed class SandboxPolicyTests
{
    private static SandboxOptions Host(Action<SandboxOptions>? configure = null)
    {
        var options = new SandboxOptions { DefaultImage = "base:1", Network = "bridge", Memory = "4g", Cpus = 2, MaxMemory = "8g", MaxCpus = 4 };
        configure?.Invoke(options);
        return options;
    }

    private static RepoProfile Repo(RepoContainer? container, string? build = null)
        => new(build, null, null, [], [], null, container);

    [Fact]
    public void NoRequest_UsesToolchainImage()
    {
        var resolved = SandboxPolicy.Resolve(Host(), Repo(null, "npm ci"));

        Assert.Equal("node:22", resolved.Image);
        Assert.Equal("toolchain 'node'", resolved.Source);
        Assert.Equal("bridge", resolved.Network);
        Assert.Equal("4g", resolved.Memory);
        Assert.Equal(2, resolved.Cpus);
    }

    [Fact]
    public void NoRequest_UnknownToolchain_UsesDefaultImage()
    {
        var resolved = SandboxPolicy.Resolve(Host(), Repo(null));

        Assert.Equal("base:1", resolved.Image);
        Assert.Equal("default image", resolved.Source);
    }

    [Fact]
    public void Profile_PicksFromTheHostMenu_AndCarriesItsOverrides()
    {
        var host = Host(o => o.Profiles["node-chromium"] = new SandboxProfile
        {
            Image = "our-registry/node-chromium:22",
            Memory = "6g",
            Network = "none",
            Volumes = ["npm-cache:/root/.npm"],
            Env = { ["PUPPETEER_SKIP_DOWNLOAD"] = "1" },
        });

        var resolved = SandboxPolicy.Resolve(host, Repo(new RepoContainer(Profile: "Node-Chromium")));

        Assert.Equal("our-registry/node-chromium:22", resolved.Image);
        Assert.Equal("profile 'Node-Chromium'", resolved.Source);
        Assert.Equal("6g", resolved.Memory);
        Assert.Equal("none", resolved.Network);
        Assert.Contains("npm-cache:/root/.npm", resolved.Volumes);
        Assert.Equal("1", resolved.Env["PUPPETEER_SKIP_DOWNLOAD"]);
    }

    [Fact]
    public void Profile_Unknown_FailsAndListsWhatExists()
    {
        var host = Host(o => o.Profiles["dotnet-sdk"] = new SandboxProfile { Image = "sdk:10" });

        var ex = Assert.Throws<SandboxException>(() => SandboxPolicy.Resolve(host, Repo(new RepoContainer(Profile: "typo"))));

        Assert.Contains("'typo'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("dotnet-sdk", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Profile_WithoutAnImage_Fails()
    {
        var host = Host(o => o.Profiles["broken"] = new SandboxProfile());

        var ex = Assert.Throws<SandboxException>(() => SandboxPolicy.Resolve(host, Repo(new RepoContainer(Profile: "broken"))));

        Assert.Contains("without an image", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Image_IsRefusedByDefault()
    {
        var ex = Assert.Throws<SandboxException>(() => SandboxPolicy.Resolve(Host(), Repo(new RepoContainer(Image: "evil/backdoor:latest"))));

        Assert.Contains("does not allow repositories to name images", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Image_AllowedWhenItMatchesTheAllowList()
    {
        var host = Host(o => o.AllowedImages = ["our-registry.corp/*", "mcr.microsoft.com/dotnet/*"]);

        var resolved = SandboxPolicy.Resolve(host, Repo(new RepoContainer(Image: "our-registry.corp/team/build:3")));

        Assert.Equal("our-registry.corp/team/build:3", resolved.Image);
        Assert.Equal("repository image", resolved.Source);
    }

    [Fact]
    public void Image_OutsideTheAllowList_FailsAndShowsThePatterns()
    {
        var host = Host(o => o.AllowedImages = ["our-registry.corp/*"]);

        var ex = Assert.Throws<SandboxException>(() => SandboxPolicy.Resolve(host, Repo(new RepoContainer(Image: "docker.io/evil:latest"))));

        Assert.Contains("not allow-listed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("our-registry.corp/*", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Profile_WinsOverImage()
    {
        var host = Host(o =>
        {
            o.Profiles["safe"] = new SandboxProfile { Image = "safe:1" };
            o.AllowedImages = ["*"];
        });

        var resolved = SandboxPolicy.Resolve(host, Repo(new RepoContainer(Profile: "safe", Image: "anything:1")));

        Assert.Equal("safe:1", resolved.Image);
    }

    [Theory]
    [InlineData("2g", "2g")]        // below the host default: honoured
    [InlineData("6g", "6g")]        // above the default but under the ceiling: honoured
    [InlineData("64g", "8g")]       // above the ceiling: clamped
    [InlineData("512m", "512m")]
    public void Memory_IsHonouredDownwardAndClampedAtTheCeiling(string requested, string expected)
    {
        var resolved = SandboxPolicy.Resolve(Host(), Repo(new RepoContainer(Memory: requested)));

        Assert.Equal(expected, resolved.Memory);
    }

    [Fact]
    public void Memory_Nonsense_Fails()
    {
        var ex = Assert.Throws<SandboxException>(() => SandboxPolicy.Resolve(Host(), Repo(new RepoContainer(Memory: "lots"))));

        Assert.Contains("not a size", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(64, 4)]
    public void Cpus_AreClampedAtTheCeiling(double requested, double expected)
    {
        Assert.Equal(expected, SandboxPolicy.Resolve(Host(), Repo(new RepoContainer(Cpus: requested))).Cpus);
    }

    [Fact]
    public void Cpus_NonPositive_Fails()
        => Assert.Throws<SandboxException>(() => SandboxPolicy.Resolve(Host(), Repo(new RepoContainer(Cpus: 0))));

    [Fact]
    public void Network_MayBeClosedButNotOpened()
    {
        Assert.Equal("none", SandboxPolicy.Resolve(Host(), Repo(new RepoContainer(Network: "none"))).Network);

        // Same value as the host is a no-op rather than an escalation.
        Assert.Equal("bridge", SandboxPolicy.Resolve(Host(), Repo(new RepoContainer(Network: "bridge"))).Network);

        var closed = Host(o => o.Network = "none");
        var ex = Assert.Throws<SandboxException>(() => SandboxPolicy.Resolve(closed, Repo(new RepoContainer(Network: "bridge"))));
        Assert.Contains("may only close the network", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Env_MergesUnderTheHost_SoTheHostAlwaysWins()
    {
        var host = Host(o => o.Env["HTTP_PROXY"] = "http://host-proxy:3128");
        var repo = Repo(new RepoContainer(Env: new Dictionary<string, string>
        {
            ["HTTP_PROXY"] = "http://attacker:8080",
            ["DOTNET_gcServer"] = "1",
        }));

        var resolved = SandboxPolicy.Resolve(host, repo);

        Assert.Equal("http://host-proxy:3128", resolved.Env["HTTP_PROXY"]);
        Assert.Equal("1", resolved.Env["DOTNET_gcServer"]);
    }

    [Fact]
    public void Volumes_ComeFromTheHostAndProfileOnly()
    {
        var host = Host(o =>
        {
            o.Volumes = ["host-cache:/cache"];
            o.Profiles["p"] = new SandboxProfile { Image = "i:1", Volumes = ["profile-cache:/npm"] };
        });

        var resolved = SandboxPolicy.Resolve(host, Repo(new RepoContainer(Profile: "p")));

        Assert.Equal(["host-cache:/cache", "profile-cache:/npm"], resolved.Volumes);
    }

    [Theory]
    [InlineData("512", 512)]
    [InlineData("512b", 512)]
    [InlineData("2k", 2048)]
    [InlineData("4m", 4194304)]
    [InlineData("1g", 1073741824)]
    [InlineData("1G", 1073741824)]
    public void TryParseMemory_UnderstandsDockerSyntax(string value, long expected)
    {
        Assert.True(SandboxPolicy.TryParseMemory(value, out var bytes));
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("lots")]
    [InlineData("-1g")]
    [InlineData("1t")]
    public void TryParseMemory_RejectsAnythingElse(string value)
        => Assert.False(SandboxPolicy.TryParseMemory(value, out _));

    [Theory]
    [InlineData("our-registry.corp/team/x:1", "our-registry.corp/*", true)]
    [InlineData("OUR-REGISTRY.corp/x", "our-registry.corp/*", true)]
    [InlineData("docker.io/x", "our-registry.corp/*", false)]
    [InlineData("node:22", "node:2?", true)]
    [InlineData("node:22", "node:1?", false)]
    [InlineData("anything", "*", true)]
    [InlineData("evil.com/our-registry.corp/x", "our-registry.corp/*", false)]
    public void MatchesGlob_IsAnchoredAndCaseInsensitive(string value, string pattern, bool expected)
        => Assert.Equal(expected, SandboxPolicy.MatchesGlob(value, pattern));
}
