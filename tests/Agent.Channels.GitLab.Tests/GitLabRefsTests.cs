using Agent.Channels.GitLab;
using Xunit;

namespace Agent.Channels.GitLab.Tests;

public sealed class GitLabRefsTests
{
    [Theory]
    [InlineData("team/repo#12", "team/repo", 12L, GitLabNoteableType.Issue)]
    [InlineData("team/sub/repo!5", "team/sub/repo", 5L, GitLabNoteableType.MergeRequest)]
    [InlineData("42#7", "42", 7L, GitLabNoteableType.Issue)]
    public void TryParse_ValidReference_ReturnsParts(string reference, string project, long iid, GitLabNoteableType type)
    {
        Assert.True(GitLabRefs.TryParse(reference, out var parsedProject, out var parsedIid, out var parsedType));
        Assert.Equal(project, parsedProject);
        Assert.Equal(iid, parsedIid);
        Assert.Equal(type, parsedType);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("PROJ-123")]
    [InlineData("#12")]
    [InlineData("team/repo#")]
    [InlineData("team/repo#abc")]
    public void TryParse_InvalidReference_ReturnsFalse(string? reference)
        => Assert.False(GitLabRefs.TryParse(reference, out _, out _, out _));

    [Fact]
    public void Build_ProducesIssueAndMergeRequestReferences()
    {
        Assert.Equal("team/repo#12", GitLabRefs.Issue("team/repo", 12));
        Assert.Equal("team/repo!7", GitLabRefs.MergeRequest("team/repo", 7));
        Assert.Equal("team/repo!7", GitLabRefs.Build(GitLabNoteableType.MergeRequest, "team/repo", 7));
    }

    [Theory]
    [InlineData("team/repo#12", "team/repo", 12L, GitLabNoteableType.Issue)]
    [InlineData("team/sub/repo!45", "team/sub/repo", 45L, GitLabNoteableType.MergeRequest)]
    [InlineData("https://gitlab.test/team/repo/-/issues/12", "team/repo", 12L, GitLabNoteableType.Issue)]
    [InlineData("https://gitlab.test/team/sub/repo/-/merge_requests/45", "team/sub/repo", 45L, GitLabNoteableType.MergeRequest)]
    [InlineData("https://gitlab.test/team/repo/-/merge_requests/45#note_9", "team/repo", 45L, GitLabNoteableType.MergeRequest)]
    [InlineData("https://gitlab.test/team/repo/-/issues/12?sort=asc", "team/repo", 12L, GitLabNoteableType.Issue)]
    [InlineData("https://gitlab.test/team/repo/issues/12", "team/repo", 12L, GitLabNoteableType.Issue)]
    [InlineData("<https://gitlab.test/team/repo/-/issues/12>", "team/repo", 12L, GitLabNoteableType.Issue)]
    public void TryParseTarget_TicketReference_ResolvesProjectAndIid(string text, string project, long iid, GitLabNoteableType type)
    {
        Assert.True(GitLabRefs.TryParseTarget(text, out var target));
        Assert.Equal(project, target.Project);
        Assert.Equal(iid, target.Iid);
        Assert.Equal(type, target.Type);
        Assert.False(target.IsRepository);
        Assert.Equal(GitLabRefs.Build(type, project, iid), target.Reference);
    }

    [Theory]
    [InlineData("team/repo")]
    [InlineData("team/sub/repo")]
    [InlineData("https://gitlab.test/team/repo")]
    [InlineData("https://gitlab.test/team/repo.git")]
    [InlineData("<https://gitlab.test/team/repo/>")]
    public void TryParseTarget_Repository_HasNoTicket(string text)
    {
        Assert.True(GitLabRefs.TryParseTarget(text, out var target));
        Assert.True(target.IsRepository);
        Assert.Null(target.Type);
        Assert.StartsWith("team/", target.Project, StringComparison.Ordinal);
        Assert.Equal(target.Project, target.Reference);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("please")]
    [InlineData("PROJ-123")]
    [InlineData("ssh://git@gitlab.test/team/repo.git")]
    public void TryParseTarget_NotAReference_ReturnsFalse(string? text)
        => Assert.False(GitLabRefs.TryParseTarget(text, out _));

    [Fact]
    public void TryParseTarget_GitLabUnderASubPath_StripsTheConfiguredBaseUrl()
    {
        Assert.True(GitLabRefs.TryParseTarget("https://host.test/gitlab/team/repo/-/issues/3", "https://host.test/gitlab", out var target));

        Assert.Equal("team/repo", target.Project);
        Assert.Equal(3, target.Iid);
        Assert.Equal(GitLabNoteableType.Issue, target.Type);
    }
}
