namespace Agent.Mcp.Tests;

public sealed class GlobMatcherTests
{
    [Theory]
    [InlineData("*", "anything", true)]
    [InlineData("*", "", true)]
    [InlineData("", "", true)]
    [InlineData("", "x", false)]
    [InlineData("exact", "exact", true)]
    [InlineData("exact", "exactly", false)]
    [InlineData("exact", "exac", false)]
    [InlineData("read*", "read_file", true)]
    [InlineData("read*", "write_file", false)]
    [InlineData("*_file", "read_file", true)]
    [InlineData("*_file", "read_files", false)]
    [InlineData("READ_FILE", "read_file", true)]
    [InlineData("e?ho", "echo", true)]
    [InlineData("e?ho", "eccho", false)]
    [InlineData("e?ho", "eho", false)]
    [InlineData("*private*", "read_private_docs", true)]
    [InlineData("*private*", "public", false)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "abc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("**", "x", true)]
    [InlineData("delete_?", "delete_1", true)]
    [InlineData("delete_?", "delete_12", false)]
    public void IsMatch_HandlesStarAndQuestionMark(string pattern, string value, bool expected)
        => Assert.Equal(expected, GlobMatcher.IsMatch(pattern, value));

    [Fact]
    public void MatchesAny_IgnoresBlankPatterns_AndTrims()
    {
        Assert.True(GlobMatcher.MatchesAny(["", "  ", " read* "], "read_file"));
        Assert.False(GlobMatcher.MatchesAny(["", "  "], "read_file"));
        Assert.False(GlobMatcher.MatchesAny([], "read_file"));
    }
}
