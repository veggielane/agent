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
}
