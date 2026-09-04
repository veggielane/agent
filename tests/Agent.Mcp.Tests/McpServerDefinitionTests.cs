using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Tools;

namespace Agent.Mcp.Tests;

public sealed class McpServerDefinitionTests
{
    private static McpServerDefinition Valid() => new() { Name = "docs", Command = "npx" };

    [Fact]
    public void Validate_ValidStdioDefinition_NoProblems()
    {
        var def = Valid();
        def.Scope = ["Answer", "coding"];
        def.Channels = ["Cli", "mattermost"];
        def.Tools.Roles["read_private*"] = "team";
        def.TimeoutSeconds = 10;

        Assert.Empty(def.Validate());
        Assert.Equal(McpTransportKind.Stdio, def.ParsedTransport);
        Assert.Equal(Role.Team, def.ParsedRole);
        Assert.Equal(ToolScope.All, def.ParsedScope);
        Assert.Equal(new HashSet<Channel> { Channel.Cli, Channel.Mattermost }, def.ParsedChannels);
    }

    [Fact]
    public void Validate_ValidHttpDefinition_NoProblems()
    {
        var def = new McpServerDefinition { Name = "inv", Transport = "http", Url = "https://mcp.internal/inventory", Headers = { ["Authorization"] = "Bearer x" } };

        Assert.Empty(def.Validate());
        Assert.Equal(McpTransportKind.Http, def.ParsedTransport);
    }

    [Fact]
    public void Validate_StdioWithoutCommand_ReportsProblem()
    {
        var def = new McpServerDefinition { Name = "docs" };

        var problem = Assert.Single(def.Validate());
        Assert.Contains("Command", problem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://files.internal/mcp")]
    [InlineData("/relative/path")]
    public void Validate_HttpWithBadUrl_ReportsProblem(string? url)
    {
        var def = new McpServerDefinition { Name = "inv", Transport = "Http", Url = url };

        var problem = Assert.Single(def.Validate());
        Assert.Contains("Url", problem);
    }

    [Fact]
    public void Validate_UnknownTransport_ReportsProblem()
    {
        var def = new McpServerDefinition { Name = "x", Transport = "WebSocket", Command = "x" };

        var problem = Assert.Single(def.Validate());
        Assert.Contains("WebSocket", problem);
    }

    [Fact]
    public void Validate_MissingName_ReportsProblem()
    {
        var def = new McpServerDefinition { Command = "x" };

        Assert.Contains(def.Validate(), p => p.Contains("Name", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("")]
    [InlineData("4")]
    public void Validate_BadRole_ReportsProblem(string role)
    {
        var def = Valid();
        def.Role = role;

        var problem = Assert.Single(def.Validate());
        Assert.Contains("role", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Chat")]
    [InlineData("None")]
    [InlineData("")]
    public void Validate_BadScope_ReportsProblem(string scope)
    {
        var def = Valid();
        def.Scope = [scope];

        var problem = Assert.Single(def.Validate());
        Assert.Contains("scope", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_BadChannel_ReportsProblem()
    {
        var def = Valid();
        def.Channels = ["Slack"];

        var problem = Assert.Single(def.Validate());
        Assert.Contains("Slack", problem);
    }

    [Fact]
    public void Validate_BadToolRole_ReportsProblem()
    {
        var def = Valid();
        def.Tools.Roles["delete_*"] = "Root";

        var problem = Assert.Single(def.Validate());
        Assert.Contains("Root", problem);
        Assert.Contains("delete_*", problem);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_NonPositiveTimeout_ReportsProblem(int seconds)
    {
        var def = Valid();
        def.TimeoutSeconds = seconds;

        var problem = Assert.Single(def.Validate());
        Assert.Contains("TimeoutSeconds", problem);
    }

    [Fact]
    public void Validate_CollectsEveryProblem()
    {
        var def = new McpServerDefinition { Name = "x", Transport = "Http", Role = "Nobody", Scope = ["Chat"], Channels = ["Slack"], TimeoutSeconds = 0 };

        Assert.Equal(5, def.Validate().Count);
    }

    [Fact]
    public void ParsedScope_Empty_IsAnswerAndCoding()
    {
        Assert.Equal(ToolScope.Answer | ToolScope.Coding, new McpServerDefinition().ParsedScope);
        Assert.Equal(ToolScope.Coding, new McpServerDefinition { Scope = ["Coding"] }.ParsedScope);
        Assert.Equal(ToolScope.All, new McpServerDefinition { Scope = ["all"] }.ParsedScope);
    }

    [Fact]
    public void ParsedChannels_UnsetOrEmpty_IsNull()
    {
        Assert.Null(new McpServerDefinition().ParsedChannels);
        Assert.Null(new McpServerDefinition { Channels = [] }.ParsedChannels);
    }

    [Fact]
    public void ParsedRole_Invalid_FallsBackToAdmin()
    {
        Assert.Equal(Role.Admin, new McpServerDefinition { Role = "nope" }.ParsedRole);
        Assert.Equal(Role.Users, new McpServerDefinition { Role = "users" }.ParsedRole);
    }

    [Fact]
    public void ToolFilter_DenyWinsOverAllow()
    {
        var filter = new McpToolFilter { Allow = ["read*"], Deny = ["read_private*"] };

        Assert.True(filter.IsAllowed("read_file"));
        Assert.False(filter.IsAllowed("read_private_file"));
        Assert.False(filter.IsAllowed("write_file"));
    }

    [Fact]
    public void ToolFilter_EmptyAllow_AllowsEverything()
    {
        Assert.True(new McpToolFilter().IsAllowed("anything"));
        Assert.False(new McpToolFilter { Deny = ["*"] }.IsAllowed("anything"));
    }

    [Fact]
    public void ToolFilter_RoleFor_PicksHighestMatchingRole()
    {
        var filter = new McpToolFilter { Roles = { ["read*"] = "Users", ["*private*"] = "Admin", ["broken"] = "Nope" } };

        Assert.Equal(Role.Users, filter.RoleFor("read_file"));
        Assert.Equal(Role.Admin, filter.RoleFor("read_private_file"));
        Assert.Null(filter.RoleFor("write_file"));
        Assert.Null(filter.RoleFor("broken"));
    }
}
