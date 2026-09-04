using System.Diagnostics;
using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Pipeline;
using Agent.Core.Tools;
using Agent.Mcp.Tests.Support;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent.Mcp.Tests;

public sealed class McpToolProviderTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task StartAsync_ConnectedServer_ExposesSanitisedToolNames()
    {
        await using var host = await ProviderHost.StartAsync(ProviderHost.Docs());

        var names = host.Provider.GetTools().Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(["docs__big", "docs__echo", "docs__secret", "docs__slow"], names);

        var status = host.Status("docs");
        Assert.Equal(McpServerState.Connected, status.State);
        Assert.Equal(4, status.ToolCount);
        Assert.Equal("connected, 4 tools", status.Summary);
        Assert.Contains(status.Tools, t => t.Name == "docs__echo" && t.ToolName == "echo" && t.Description == "Echoes text back");

        var echo = host.Tool("docs__echo");
        Assert.Equal("docs", echo.Source);
        Assert.Equal(Role.Team, echo.Role);
        Assert.Equal(ToolScope.All, echo.Scope);
        Assert.Null(echo.Channels);
        Assert.Equal("Echoes text back", echo.Function.Description);
    }

    [Fact]
    public async Task Deny_HidesMatchingTools()
    {
        await using var host = await ProviderHost.StartAsync(ProviderHost.Docs(d => d.Tools.Deny = ["secret"]));

        var names = host.Provider.GetTools().Select(t => t.Name).ToList();
        Assert.DoesNotContain("docs__secret", names);
        Assert.Equal(3, names.Count);
        Assert.DoesNotContain(host.Status("docs").Tools, t => t.ToolName == "secret");
    }

    [Fact]
    public async Task Allow_RestrictsToMatchingTools()
    {
        await using var host = await ProviderHost.StartAsync(ProviderHost.Docs(d => d.Tools.Allow = ["e?ho", "B*"]));

        var names = host.Provider.GetTools().Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(["docs__big", "docs__echo"], names);
    }

    [Fact]
    public async Task ToolRoleOverride_RaisesRoleForMatchingTool()
    {
        await using var host = await ProviderHost.StartAsync(ProviderHost.Docs(d => d.Tools.Roles["slow"] = "Admin"));

        Assert.Equal(Role.Admin, host.Tool("docs__slow").Role);
        Assert.Equal(Role.Team, host.Tool("docs__echo").Role);
        Assert.Equal(Role.Admin, host.Status("docs").Tools.Single(t => t.ToolName == "slow").Role);
    }

    [Fact]
    public async Task ToolRegistry_FiltersByCallerRole()
    {
        await using var host = await ProviderHost.StartAsync(ProviderHost.Docs(d => d.Tools.Roles["slow"] = "Admin"));
        var registry = new ToolRegistry([host.Provider], NullLogger<ToolRegistry>.Instance);

        Assert.Empty(registry.GetTools(CallerIdentity.Local("u", Role.Users), ToolScope.Answer, Channel.Cli));
        Assert.Equal(3, registry.GetTools(CallerIdentity.Local("t", Role.Team), ToolScope.Answer, Channel.Cli).Count);
        Assert.Equal(4, registry.GetTools(CallerIdentity.Local("a", Role.Admin), ToolScope.Answer, Channel.Cli).Count);
    }

    [Fact]
    public async Task ChannelsAndScope_RestrictWhereToolsAreOffered()
    {
        await using var host = await ProviderHost.StartAsync(ProviderHost.Docs(d =>
        {
            d.Channels = ["mattermost"];
            d.Scope = ["coding"];
        }));
        var registry = new ToolRegistry([host.Provider], NullLogger<ToolRegistry>.Instance);
        var admin = CallerIdentity.Local("a", Role.Admin);

        Assert.Equal(new HashSet<Channel> { Channel.Mattermost }, host.Tool("docs__echo").Channels);
        Assert.Equal(ToolScope.Coding, host.Tool("docs__echo").Scope);
        Assert.Empty(registry.GetTools(admin, ToolScope.Coding, Channel.Cli));
        Assert.Empty(registry.GetTools(admin, ToolScope.Answer, Channel.Mattermost));
        Assert.Equal(4, registry.GetTools(admin, ToolScope.Coding, Channel.Mattermost).Count);
    }

    [Fact]
    public async Task Invoke_Echo_ReturnsServerResult()
    {
        await using var host = await ProviderHost.StartAsync(ProviderHost.Docs());

        var result = await host.Tool("docs__echo").Function.InvokeAsync(new AIFunctionArguments { ["text"] = "hi" }, Ct);

        Assert.Contains("echo: hi", result?.ToString());
    }

    [Fact]
    public async Task Invoke_Big_TruncatesToMaxResultChars()
    {
        await using var host = await ProviderHost.StartAsync(o => o.MaxResultChars = 1000, null, ProviderHost.Docs());

        var result = (await host.Tool("docs__big").Function.InvokeAsync(new AIFunctionArguments(), Ct))?.ToString();

        Assert.NotNull(result);
        Assert.Contains("[truncated", result);
        Assert.InRange(result.Length, 1000, 1100);
    }

    [Fact]
    public async Task Invoke_Slow_TimesOutAfterServerTimeout()
    {
        await using var host = await ProviderHost.StartAsync(ProviderHost.Docs(d => d.TimeoutSeconds = 1));

        var sw = Stopwatch.StartNew();
        var result = (await host.Tool("docs__slow").Function.InvokeAsync(new AIFunctionArguments { ["ms"] = 3000 }, Ct))?.ToString();
        sw.Stop();

        Assert.Contains("timed out after 1s", result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2.5), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task Invoke_WritesAuditEntryForCaller()
    {
        await using var host = await ProviderHost.StartAsync(ProviderHost.Docs());

        using (RequestContext.Begin(CallerIdentity.Local("alice", Role.Team)))
        {
            await host.Tool("docs__echo").Function.InvokeAsync(new AIFunctionArguments { ["text"] = "audit me" }, Ct);
        }

        var entry = Assert.Single(host.Audit.Entries, e => e.Action == "tool:docs__echo");
        Assert.Equal("ok", entry.Outcome);
        Assert.Equal("alice", entry.CallerId);
        Assert.Contains("source=docs", entry.Detail);
        Assert.Contains("audit me", entry.Detail);
        Assert.Contains(Role.Team, entry.Roles!);
    }

    [Fact]
    public async Task Reload_PicksUpDefinitionChange()
    {
        await using var host = await ProviderHost.StartAsync(o => o.Directory = "temp");
        var dir = host.Directory!;
        dir.Write("docs.json", """{ "Transport": "Stdio", "Command": "fake" }""");

        await host.Provider.ReloadAsync(Ct);
        Assert.Contains(host.Provider.GetTools(), t => t.Name == "docs__echo");
        var firstServer = host.Connector.LastServer;

        dir.Delete("docs.json");
        dir.Write("wiki.json", """{ "Transport": "Stdio", "Command": "fake", "Tools": { "Deny": ["secret"] } }""");
        await host.Provider.ReloadAsync(Ct);

        var names = host.Provider.GetTools().Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(["wiki__big", "wiki__echo", "wiki__slow"], names);
        Assert.Single(host.Provider.Servers);
        Assert.Equal("wiki", host.Provider.Servers[0].Name);
        await Wait.UntilAsync(() => firstServer.IsStopped, because: "the replaced server session should be closed");
    }

    [Fact]
    public async Task ConnectFailure_YieldsErrorStatusWithoutTools()
    {
        var connector = new InProcessConnector { FailWith = _ => new IOException("boom") };
        await using var host = await ProviderHost.StartAsync(_ => { }, connector, ProviderHost.Docs());

        var status = host.Status("docs");
        Assert.Equal(McpServerState.Error, status.State);
        Assert.Equal("boom", status.Error);
        Assert.Equal("error: boom", status.Summary);
        Assert.Empty(host.Provider.GetTools());
    }

    [Fact]
    public async Task ConnectTimeout_YieldsErrorStatus()
    {
        var connector = new HangingConnector();
        await using var host = await ProviderHost.StartAsync(o => o.DefaultTimeoutSeconds = 1, null, ProviderHost.Docs());
        var provider = new McpToolProvider(
            new McpDefinitionLoader(new TestOptionsMonitor<McpOptions>(host.Options), NullLogger<McpDefinitionLoader>.Instance),
            connector,
            new TestOptionsMonitor<McpOptions>(host.Options),
            host.Audit,
            NullLogger<McpToolProvider>.Instance);
        await using var _ = provider;

        var sw = Stopwatch.StartNew();
        await provider.StartAsync(Ct);

        var status = provider.Servers.Single();
        Assert.Equal(McpServerState.Error, status.State);
        Assert.Contains("timed out", status.Error);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task ToolsListChanged_RefreshesToolsForThatServer()
    {
        await using var host = await ProviderHost.StartAsync(ProviderHost.Docs());
        var server = host.Connector.LastServer;

        await server.AddToolAsync(TestTools.Named("extra"), Ct);

        await Wait.UntilAsync(() => host.Provider.GetTools().Any(t => t.Name == "docs__extra"), because: "tools/list_changed should trigger a re-list");
        Assert.Equal(5, host.Status("docs").ToolCount);
        Assert.Contains("hello from extra", await host.Provider.CallToolAsync("docs", "extra", "{}", Ct));
    }

    [Fact]
    public async Task ReconnectNowAsync_AfterFailure_Connects()
    {
        var connector = new InProcessConnector { FailWith = _ => new IOException("boom") };
        await using var host = await ProviderHost.StartAsync(_ => { }, connector, ProviderHost.Docs());
        Assert.Equal(McpServerState.Error, host.Status("docs").State);

        connector.FailWith = _ => null;
        await host.Provider.ReconnectNowAsync(Ct);

        Assert.Equal(McpServerState.Connected, host.Status("docs").State);
        Assert.Equal(4, host.Provider.GetTools().Count());
        Assert.Equal(2, connector.ConnectCount);
    }

    [Fact]
    public async Task ReconnectNowAsync_AfterServerDrop_Reconnects()
    {
        await using var host = await ProviderHost.StartAsync(ProviderHost.Docs());
        var first = host.Connector.LastServer;

        await first.DisposeAsync();

        await Wait.UntilAsync(async () =>
        {
            await host.Provider.ReconnectNowAsync(Ct);
            return host.Connector.ConnectCount == 2;
        }, because: "a dropped session should be re-dialed");
        Assert.Equal(McpServerState.Connected, host.Status("docs").State);
        Assert.Contains("echo: again", (await host.Provider.CallToolAsync("docs", "echo", """{"text":"again"}""", Ct)));
    }

    [Fact]
    public async Task DisabledServer_IsNotConnected()
    {
        await using var host = await ProviderHost.StartAsync(ProviderHost.Docs(d => d.Enabled = false));

        Assert.Equal(McpServerState.Disabled, host.Status("docs").State);
        Assert.Equal("disabled", host.Status("docs").Summary);
        Assert.Equal(0, host.Connector.ConnectCount);
        Assert.Empty(host.Provider.GetTools());
    }

    [Fact]
    public async Task InvalidDefinition_IsReportedAsInvalid()
    {
        var broken = new McpServerDefinition { Name = "broken", Transport = "Http" };
        await using var host = await ProviderHost.StartAsync(ProviderHost.Docs(), broken);

        var status = host.Status("broken");
        Assert.Equal(McpServerState.Invalid, status.State);
        Assert.Contains("Url", status.Error);
        Assert.Contains(host.Provider.Problems, p => p.Server == "broken");
        Assert.Equal(1, host.Connector.ConnectCount);
    }

    [Fact]
    public async Task CallToolAsync_InvokesWithJsonArguments()
    {
        await using var host = await ProviderHost.StartAsync(ProviderHost.Docs());

        var byToolName = await host.Provider.CallToolAsync("docs", "echo", """{ "text": "hello" }""", Ct);
        var bySanitisedName = await host.Provider.CallToolAsync("DOCS", "docs__echo", """{"text":"again"}""", Ct);

        Assert.Contains("echo: hello", byToolName);
        Assert.Contains("echo: again", bySanitisedName);
    }

    [Fact]
    public async Task CallToolAsync_UnknownServerOrTool_Throws()
    {
        await using var host = await ProviderHost.StartAsync(ProviderHost.Docs());

        var noServer = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Provider.CallToolAsync("nope", "echo", "{}", Ct));
        var noTool = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Provider.CallToolAsync("docs", "nope", "{}", Ct));

        Assert.Contains("nope", noServer.Message);
        Assert.Contains("echo", noTool.Message);
    }

    [Fact]
    public async Task CallToolAsync_InvalidJson_ThrowsArgumentException()
    {
        await using var host = await ProviderHost.StartAsync(ProviderHost.Docs());

        await Assert.ThrowsAsync<ArgumentException>(() => host.Provider.CallToolAsync("docs", "echo", "{not json", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Provider.CallToolAsync("docs", "echo", "[1, 2]", Ct));
    }

    [Fact]
    public void ParseArguments_EmptyOrObject_BuildsArguments()
    {
        Assert.Empty(McpToolProvider.ParseArguments(string.Empty));
        Assert.Empty(McpToolProvider.ParseArguments("  "));

        var args = McpToolProvider.ParseArguments("""{ "a": 1, "b": "two", "c": [3] }""");

        Assert.Equal(3, args.Count);
        Assert.Equal("1", args["a"]?.ToString());
        Assert.Equal("two", args["b"]?.ToString());
    }

    [Fact]
    public async Task GetStatusAsync_SummarisesEveryServer()
    {
        var connector = new InProcessConnector { FailWith = d => d.Name == "bad" ? new IOException("boom") : null };
        var bad = new McpServerDefinition { Name = "bad", Command = "x" };
        await using var host = await ProviderHost.StartAsync(_ => { }, connector, ProviderHost.Docs(), bad);

        var line = await host.Provider.GetStatusAsync(Ct);

        Assert.Equal("mcp", host.Provider.Name);
        Assert.Contains("docs connected, 4 tools", line);
        Assert.Contains("bad error: boom", line);
    }

    [Fact]
    public async Task StopAsync_ClosesConnectionsAndClearsTools()
    {
        await using var host = await ProviderHost.StartAsync(ProviderHost.Docs());
        var server = host.Connector.LastServer;

        await host.Provider.StopAsync(Ct);

        Assert.Empty(host.Provider.GetTools());
        Assert.Empty(host.Provider.Servers);
        await Wait.UntilAsync(() => server.IsStopped, because: "stopping the provider should end the session");
    }

    /// <summary>Never completes the connect, so the provider's own timeout has to fire.</summary>
    private sealed class HangingConnector : IMcpClientConnector
    {
        public async Task<IMcpConnection> ConnectAsync(McpServerDefinition definition, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }
    }
}
