using Agent.Channels.GitLab;
using Xunit;

namespace Agent.Channels.GitLab.Tests;

public sealed class GitLabCredentialProviderTests
{
    private static GitLabCredentialProvider Provider(string baseUrl = "https://gitlab.internal", string token = "glpat-secret")
        => new(TestOptions.Monitor(new GitLabOptions { BaseUrl = baseUrl, Token = token }));

    [Theory]
    [InlineData("https://gitlab.internal/team/repo.git")]
    [InlineData("https://GITLAB.INTERNAL/team/repo.git")]
    [InlineData("http://gitlab.internal:8080/team/repo")]
    public void GetCredentials_SameHost_ReturnsOauth2AndToken(string repoUrl)
        => Assert.Equal(("oauth2", "glpat-secret"), Provider().GetCredentials(repoUrl));

    [Theory]
    [InlineData("https://github.com/team/repo.git")]
    [InlineData("https://gitlab.internal.evil.test/team/repo.git")]
    [InlineData("git@gitlab.internal:team/repo.git")]
    [InlineData("not a url")]
    [InlineData("")]
    public void GetCredentials_OtherHostOrInvalidUrl_ReturnsNull(string repoUrl)
        => Assert.Null(Provider().GetCredentials(repoUrl));

    [Fact]
    public void GetCredentials_WithoutToken_ReturnsNull()
        => Assert.Null(Provider(token: "").GetCredentials("https://gitlab.internal/team/repo.git"));

    [Fact]
    public void GetCredentials_BaseUrlWithPath_MatchesOnHostOnly()
        => Assert.Equal(("oauth2", "glpat-secret"), Provider("https://gitlab.internal/gitlab").GetCredentials("https://gitlab.internal/gitlab/team/repo.git"));
}
