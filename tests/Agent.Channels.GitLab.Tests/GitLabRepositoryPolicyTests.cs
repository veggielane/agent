using Agent.Channels.GitLab;
using Xunit;

namespace Agent.Channels.GitLab.Tests;

public sealed class GitLabRepositoryPolicyTests
{
    private static GitLabRepositoryPolicy Policy(string baseUrl = "https://gitlab.test", params string[] allowed)
    {
        var options = TestOptions.Default(baseUrl);
        options.AllowedProjects = allowed;
        return new GitLabRepositoryPolicy(TestOptions.Monitor(options));
    }

    [Fact]
    public void Check_NoAllowList_AllowsAnything()
    {
        Assert.True(Policy().Check("https://elsewhere.test/any/repo.git").Allowed);
        Assert.True(Policy("https://gitlab.test", " ", "").Check("https://gitlab.test/team/repo.git").Allowed);
    }

    [Theory]
    [InlineData("https://gitlab.test/platform/billing.git")]
    [InlineData("https://gitlab.test/platform/billing")]
    [InlineData("https://gitlab.test/Platform/Sub/Service.git")]
    [InlineData("https://gitlab.test/tools/cli.git")]
    public void Check_ProjectUnderAnAllowedPath_IsAllowed(string url)
    {
        Assert.True(Policy("https://gitlab.test", "platform", "tools/cli").Check(url).Allowed);
    }

    [Theory]
    [InlineData("https://gitlab.test/other/repo.git")]
    [InlineData("https://gitlab.test/platformx/repo.git")]
    [InlineData("https://gitlab.test/tools/cli-extras.git")]
    [InlineData("https://gitlab.test/tools.git")]
    public void Check_ProjectOutsideTheAllowedPaths_IsRefusedWithTheProjectNamed(string url)
    {
        var decision = Policy("https://gitlab.test", "platform", "tools/cli").Check(url);

        Assert.False(decision.Allowed);
        Assert.Contains("GitLab:AllowedProjects", decision.Reason, StringComparison.Ordinal);
        Assert.Contains(new Uri(url).AbsolutePath.Trim('/').Replace(".git", string.Empty, StringComparison.Ordinal), decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_OtherHost_IsRefusedEvenWhenThePathMatches()
    {
        var decision = Policy("https://gitlab.test", "platform").Check("https://github.com/platform/billing.git");

        Assert.False(decision.Allowed);
        Assert.Contains("gitlab.test", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("github.com", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_NotAUrl_IsRefused()
    {
        Assert.False(Policy("https://gitlab.test", "platform").Check("platform/billing").Allowed);
    }

    [Fact]
    public void Check_GitLabUnderARelativeRoot_StripsTheRootBeforeMatching()
    {
        var policy = Policy("https://host.test/gitlab", "platform");

        Assert.True(policy.Check("https://host.test/gitlab/platform/billing.git").Allowed);
        Assert.False(policy.Check("https://host.test/platform/billing.git").Allowed);
    }

    [Theory]
    [InlineData("https://gitlab.test/platform/billing.git", "https://gitlab.test", "platform/billing")]
    [InlineData("https://gitlab.test/platform/billing", "https://gitlab.test/", "platform/billing")]
    [InlineData("https://host.test/gitlab/a/b.git", "https://host.test/gitlab", "a/b")]
    [InlineData("https://host.test/a/b.git", "https://host.test/gitlab", null)]
    public void ProjectPath_StripsRootLeadingSlashAndGitSuffix_OrIsNullOutsideTheRoot(string repo, string gitlab, string? expected)
    {
        Assert.Equal(expected, GitLabRepositoryPolicy.ProjectPath(new Uri(repo), new Uri(gitlab)));
    }

    [Fact]
    public void Check_UrlOutsideTheGitLabRoot_IsRefusedAsNotOnThisInstance()
    {
        var decision = Policy("https://host.test/gitlab", "platform").Check("https://host.test/platform/billing.git");

        Assert.False(decision.Allowed);
        Assert.Contains("not under the GitLab instance at `https://host.test/gitlab`", decision.Reason, StringComparison.Ordinal);
    }
}
