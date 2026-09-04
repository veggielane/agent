namespace Agent.Mcp.Tests;

public sealed class McpToolNamingTests
{
    [Theory]
    [InlineData("docs", "echo", "docs__echo")]
    [InlineData("Docs-1", "Read_File", "Docs-1__Read_File")]
    [InlineData("my server", "get.item", "my_server__get_item")]
    [InlineData("a/b", "c:d", "a_b__c_d")]
    [InlineData("gitlab", "issues/list@v2", "gitlab__issues_list_v2")]
    [InlineData("ünïcode", "tool", "_n_code__tool")]
    [InlineData("", "", "__")]
    public void Build_SanitisesServerAndTool(string server, string tool, string expected)
        => Assert.Equal(expected, McpToolNaming.Build(server, tool));

    [Theory]
    [InlineData("", "_")]
    [InlineData("   ", "___")]
    [InlineData("ok_name-1", "ok_name-1")]
    [InlineData("a b", "a_b")]
    public void Sanitise_ReplacesDisallowedCharacters(string raw, string expected)
        => Assert.Equal(expected, McpToolNaming.Sanitise(raw));

    [Fact]
    public void Build_TrimsTo64Characters()
    {
        var server = new string('s', 40);
        var tool = new string('t', 40);

        var name = McpToolNaming.Build(server, tool);

        Assert.Equal(64, name.Length);
        Assert.StartsWith(server + "__", name);
        Assert.Matches("^[A-Za-z0-9_-]{1,64}$", name);
    }

    [Theory]
    [InlineData("docs", "echo")]
    [InlineData("jira (prod)", "issue.search")]
    [InlineData("!!!", "???")]
    public void Build_AlwaysMatchesFunctionNameRule(string server, string tool)
        => Assert.Matches("^[A-Za-z0-9_-]{1,64}$", McpToolNaming.Build(server, tool));
}
