using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Pipeline;
using Agent.Core.Tests.Support;
using Agent.Core.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Core.Tests.Commands;

public sealed class CommandToolSourceTests
{
    [Fact]
    public async Task ExposedCommand_BecomesTool_AndRunsInRequestContext()
    {
        using var host = TestHost.Create(s => s.AddCommandHandlers<SampleCommands>());
        var registry = host.Get<IToolRegistry>();

        var tool = registry.Find("cmd_tool");
        Assert.NotNull(tool);
        Assert.Equal("command", tool.Source);
        Assert.Equal(Role.Users, tool.Role);
        Assert.Null(registry.Find("cmd_greet"));

        using var scope = RequestContext.Begin(CallerIdentity.Local("tester", Role.Users));
        var result = await tool.Function.InvokeAsync(new AIFunctionArguments { ["arguments"] = "hello there" }, TestContext.Current.CancellationToken);

        Assert.Equal("tool:hello there", result?.ToString());
    }

    [Fact]
    public void Registry_FiltersByRoleScopeAndChannel()
    {
        using var host = TestHost.Create(s => s.AddCommandHandlers<SampleCommands>());
        var registry = host.Get<IToolRegistry>();

        var users = registry.GetTools(CallerIdentity.Local("u", Role.Users), ToolScope.Answer, Channel.Cli);
        Assert.Contains(users, t => t.Name == "cmd_tool");

        var coding = registry.GetTools(CallerIdentity.Local("u", Role.Users), ToolScope.Coding, Channel.Cli);
        Assert.DoesNotContain(coding, t => t.Name == "cmd_tool");

        var none = registry.GetTools(new CallerIdentity(Channel.Cli, "nobody"), ToolScope.Answer, Channel.Cli);
        Assert.Empty(none);
    }
}
