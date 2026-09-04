using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Commands;
using Agent.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Agent.Mcp.Tests;

public sealed class McpCommandsTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private readonly IMcpToolProvider _provider = Substitute.For<IMcpToolProvider>();
    private readonly CommandDescriptor _command;
    private readonly IServiceProvider _services;

    public McpCommandsTests()
    {
        _provider.Servers.Returns(
        [
            new McpServerStatus(
                "docs",
                McpServerState.Connected,
                2,
                null,
                [
                    new McpToolInfo("docs__echo", "echo", "Echoes text | back", Role.Team),
                    new McpToolInfo("docs__slow", "slow", "Waits", Role.Admin),
                ],
                "Stdio",
                Role.Team,
                ToolScope.All),
            new McpServerStatus("inventory", McpServerState.Error, 0, "connection refused", [], "Http", Role.Users, ToolScope.Answer),
        ]);
        _provider.Problems.Returns([new McpDefinitionProblem(null, "C:\\agent\\mcp\\broken.json", "bad json")]);

        _services = new ServiceCollection().AddSingleton(_provider).BuildServiceProvider();
        _command = AttributedCommandSource.FromType(typeof(McpCommands), _services).Single();
    }

    [Fact]
    public void Descriptor_IsNamedMcp_AndRequiresTeam()
    {
        Assert.Equal("mcp", _command.Name);
        Assert.Equal(Role.Team, _command.Role);
        Assert.Equal("!mcp <action> [server] [tool] [json]...", _command.Usage());
    }

    [Fact]
    public async Task List_TeamCaller_ShowsServersAndProblems()
    {
        var result = await RunAsync(Role.Team, "list");

        Assert.False(result.IsError);
        Assert.Contains("| docs | connected | 2 | Team | Answer, Coding | Stdio | - |", result.Markdown);
        Assert.Contains("| inventory | error | 0 | Users | Answer | Http | connection refused |", result.Markdown);
        Assert.Contains("broken.json: bad json", result.Markdown);
    }

    [Fact]
    public async Task Tools_TeamCaller_ListsToolsWithRoles()
    {
        var result = await RunAsync(Role.Team, "tools docs");

        Assert.False(result.IsError);
        Assert.Contains("**docs** - connected, 2 tools", result.Markdown);
        Assert.Contains("| `docs__echo` | Team | Echoes text \\| back |", result.Markdown);
        Assert.Contains("| `docs__slow` | Admin | Waits |", result.Markdown);
    }

    [Fact]
    public async Task Tools_UnknownServer_ReturnsError()
    {
        var result = await RunAsync(Role.Team, "tools nope");

        Assert.True(result.IsError);
        Assert.Contains("nope", result.Markdown);
    }

    [Fact]
    public async Task Tools_WithoutServer_ReturnsUsage()
    {
        var result = await RunAsync(Role.Team, "tools");

        Assert.True(result.IsError);
        Assert.Contains("mcp tools <server>", result.Markdown);
    }

    [Fact]
    public async Task Reload_TeamCaller_Denied()
    {
        var result = await RunAsync(Role.Team, "reload");

        Assert.True(result.IsError);
        Assert.Contains("Admin", result.Markdown);
        await _provider.DidNotReceive().ReloadAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reload_AdminCaller_ReloadsAndLists()
    {
        var result = await RunAsync(Role.Admin, "reload");

        Assert.False(result.IsError);
        Assert.Contains("reloaded", result.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("| docs |", result.Markdown);
        await _provider.Received(1).ReloadAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Call_TeamCaller_Denied()
    {
        var result = await RunAsync(Role.Team, """call docs echo {"text":"hi"}""");

        Assert.True(result.IsError);
        await _provider.DidNotReceive().CallToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Call_AdminCaller_PassesJsonThroughUntouched()
    {
        _provider.CallToolAsync("docs", "echo", """{"text": "hi there", "n": 2}""", Arg.Any<CancellationToken>()).Returns("echo: hi there");

        var result = await RunAsync(Role.Admin, """call docs echo {"text": "hi there", "n": 2}""");

        Assert.False(result.IsError);
        Assert.Contains("echo: hi there", result.Markdown);
        await _provider.Received(1).CallToolAsync("docs", "echo", """{"text": "hi there", "n": 2}""", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Call_WithoutArguments_PassesEmptyJson()
    {
        _provider.CallToolAsync("docs", "big", string.Empty, Arg.Any<CancellationToken>()).Returns("xxx");

        var result = await RunAsync(Role.Admin, "call docs big");

        Assert.False(result.IsError);
        Assert.Contains("xxx", result.Markdown);
    }

    [Fact]
    public async Task Call_MissingTool_ReturnsUsage()
    {
        var result = await RunAsync(Role.Admin, "call docs");

        Assert.True(result.IsError);
        Assert.Contains("mcp call", result.Markdown);
    }

    [Fact]
    public async Task Call_ProviderRejects_ReturnsErrorMessage()
    {
        _provider.CallToolAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("No MCP server 'nope'."));

        var result = await RunAsync(Role.Admin, "call nope echo {}");

        Assert.True(result.IsError);
        Assert.Equal("No MCP server 'nope'.", result.Markdown);
    }

    [Fact]
    public async Task UnknownAction_ReturnsError()
    {
        var result = await RunAsync(Role.Team, "dance");

        Assert.True(result.IsError);
        Assert.Contains("dance", result.Markdown);
    }

    private Task<CommandResult> RunAsync(Role role, string args)
    {
        var ctx = new CommandContext
        {
            Caller = CallerIdentity.Local("alice", role),
            Channel = Channel.Cli,
            ConversationId = "conv",
            Services = _services,
            CancellationToken = Ct,
            RawArgs = args,
            CommandName = "mcp",
        };
        return _command.Invoke(ctx, CommandBinder.Parse(_command, args));
    }
}
