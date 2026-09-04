using Agent.Core.Channels;
using Agent.Core.Commands;
using Agent.Core.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Core.Tests.Commands;

public sealed class CommandDispatcherTests
{
    private static TestHost Host(IDictionary<string, string?>? config = null) => TestHost.Create(s => s.AddCommandHandlers<SampleCommands>(), config);

    [Theory]
    [InlineData("!ping", true)]
    [InlineData("  !ping", true)]
    [InlineData("!", false)]
    [InlineData("!!", false)]
    [InlineData("hello !ping", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCommand_DetectsPrefix(string? text, bool expected)
    {
        using var host = Host();
        Assert.Equal(expected, host.Get<ICommandDispatcher>().IsCommand(text));
    }

    [Fact]
    public async Task Dispatch_RunsCommand_AndAudits()
    {
        using var host = Host();
        var caller = await host.ResolveAsync("alice");
        var result = await host.Get<ICommandDispatcher>().DispatchAsync("!greet world --loud", host.CommandContext(caller));

        Assert.False(result.IsError);
        Assert.Equal("WORLD", result.Markdown);
        Assert.Contains(host.Audit.Entries, e => e.Action == "command:greet" && e.Outcome == "ok");
    }

    [Fact]
    public async Task Dispatch_UnknownCommand_ReturnsHint()
    {
        using var host = Host();
        var caller = await host.ResolveAsync("alice");
        var result = await host.Get<ICommandDispatcher>().DispatchAsync("!nothing", host.CommandContext(caller));

        Assert.True(result.IsError);
        Assert.Contains("!help", result.Markdown);
    }

    [Fact]
    public async Task Dispatch_UnknownCommand_SilentWhenConfigured()
    {
        using var host = Host(new Dictionary<string, string?> { ["Commands:ReplyToUnknown"] = "false" });
        var caller = await host.ResolveAsync("alice");
        var result = await host.Get<ICommandDispatcher>().DispatchAsync("!nothing", host.CommandContext(caller));
        Assert.True(result.Silent);
    }

    [Fact]
    public async Task Dispatch_RoleDenied_PublicChannel_IsSilent()
    {
        using var host = Host();
        var caller = await host.ResolveAsync("alice");
        var evt = host.Event("!teamonly", "alice");
        var result = await host.Get<ICommandDispatcher>().DispatchAsync("!teamonly", host.CommandContext(caller, evt: evt));

        Assert.True(result.Silent);
        Assert.Contains(host.Audit.Entries, e => e.Action == "command:teamonly" && e.Outcome == "deny");
    }

    [Fact]
    public async Task Dispatch_RoleDenied_Private_GetsRefusal()
    {
        using var host = Host();
        var caller = await host.ResolveAsync("alice");
        var evt = host.Event("!teamonly", "alice", isPrivate: true);
        var result = await host.Get<ICommandDispatcher>().DispatchAsync("!teamonly", host.CommandContext(caller, evt: evt));

        Assert.True(result.IsError);
        Assert.Contains("not authorised", result.Markdown);
    }

    [Fact]
    public async Task Dispatch_RoleDenied_ReplyBehaviour_GetsRefusal()
    {
        using var host = Host(new Dictionary<string, string?> { ["Authorization:DenyBehaviour"] = "Reply" });
        var caller = await host.ResolveAsync("alice");
        var evt = host.Event("!teamonly", "alice");
        var result = await host.Get<ICommandDispatcher>().DispatchAsync("!teamonly", host.CommandContext(caller, evt: evt));
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Dispatch_TeamCaller_RunsTeamCommand_ViaAlias()
    {
        using var host = Host();
        var caller = await host.ResolveAsync("bob");
        var result = await host.Get<ICommandDispatcher>().DispatchAsync("!to", host.CommandContext(caller));
        Assert.Equal("team", result.Markdown);
    }

    [Fact]
    public async Task Dispatch_BindingError_ShowsUsage()
    {
        using var host = Host();
        var caller = await host.ResolveAsync("alice");
        var result = await host.Get<ICommandDispatcher>().DispatchAsync("!greet", host.CommandContext(caller));

        Assert.True(result.IsError);
        Assert.Contains("Usage: `!greet <name>", result.Markdown);
    }

    [Fact]
    public async Task Dispatch_Exception_ReturnsReference()
    {
        using var host = Host();
        var caller = await host.ResolveAsync("alice");
        var result = await host.Get<ICommandDispatcher>().DispatchAsync("!boom", host.CommandContext(caller));

        Assert.True(result.IsError);
        Assert.Matches(@"ref [0-9a-f]{6}", result.Markdown);
        Assert.Contains(host.Audit.Entries, e => e.Action == "command:boom" && e.Outcome == "exception");
    }

    [Fact]
    public async Task Dispatch_ChannelRestriction_Enforced()
    {
        using var host = Host();
        var caller = await host.ResolveAsync("alice", Channel.Jira);
        var result = await host.Get<ICommandDispatcher>().DispatchAsync("!mmonly", host.CommandContext(caller));

        Assert.True(result.IsError);
        Assert.Contains("not available on Jira", result.Markdown);
    }

    [Fact]
    public async Task Dispatch_CustomPrefix_Works()
    {
        using var host = Host(new Dictionary<string, string?> { ["Commands:Prefix"] = "$" });
        var dispatcher = host.Get<ICommandDispatcher>();
        Assert.False(dispatcher.IsCommand("!ping"));
        Assert.True(dispatcher.IsCommand("$ping"));

        var caller = await host.ResolveAsync("alice");
        Assert.Equal("pong", (await dispatcher.DispatchAsync("$ping", host.CommandContext(caller))).Markdown);
    }
}
