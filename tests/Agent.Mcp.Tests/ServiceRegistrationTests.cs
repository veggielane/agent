using Agent.Core.Audit;
using Agent.Core.Commands;
using Agent.Core.Infrastructure;
using Agent.Core.Tools;
using Agent.Mcp.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agent.Mcp.Tests;

public sealed class ServiceRegistrationTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

    private static ServiceProvider Build(IConfiguration config, Action<IServiceCollection>? before = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAuditSink>(new InMemoryAuditSink());
        before?.Invoke(services);
        services.AddAgentMcp(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task AddAgentMcp_RegistersOneProviderUnderEveryInterface()
    {
        await using var sp = Build(Config());

        var provider = sp.GetRequiredService<McpToolProvider>();
        Assert.Same(provider, sp.GetRequiredService<IMcpToolProvider>());
        Assert.Same(provider, Assert.Single(sp.GetServices<IToolSource>()));
        Assert.Same(provider, Assert.Single(sp.GetServices<IHostedService>()));
        Assert.Same(provider, Assert.Single(sp.GetServices<IReloadable>()));
        Assert.Same(provider, Assert.Single(sp.GetServices<IStatusContributor>()));
        Assert.IsType<SdkMcpClientConnector>(sp.GetRequiredService<IMcpClientConnector>());
        Assert.Contains(sp.GetServices<CommandHandlerRegistration>(), r => r.HandlerType == typeof(McpCommands));
    }

    [Fact]
    public async Task AddAgentMcp_BindsOptionsFromMcpSection()
    {
        await using var sp = Build(Config(
            ("Mcp:Directory", "servers"),
            ("Mcp:MaxResultChars", "1234"),
            ("Mcp:Servers:inventory:Transport", "Http"),
            ("Mcp:Servers:inventory:Url", "https://mcp.internal/inventory"),
            ("Mcp:Servers:inventory:Headers:Authorization", "Bearer x"),
            ("Mcp:Servers:inventory:Tools:Deny:0", "delete_*"),
            ("Mcp:Servers:inventory:Tools:Roles:read_private*", "Admin")));

        var options = sp.GetRequiredService<IOptionsMonitor<McpOptions>>().CurrentValue;

        Assert.Equal("servers", options.Directory);
        Assert.Equal(1234, options.MaxResultChars);
        var inventory = options.Servers["inventory"];
        Assert.Equal("https://mcp.internal/inventory", inventory.Url);
        Assert.Equal("Bearer x", inventory.Headers["Authorization"]);
        Assert.Equal(["delete_*"], inventory.Tools.Deny);
        Assert.Equal("Admin", inventory.Tools.Roles["read_private*"]);
    }

    [Fact]
    public async Task AddAgentMcp_ArraysFromConfiguration_NarrowRatherThanAppend()
    {
        await using var sp = Build(Config(
            ("Mcp:Servers:docs:Command", "npx"),
            ("Mcp:Servers:docs:Scope:0", "Coding"),
            ("Mcp:Servers:docs:Tools:Allow:0", "read*")));

        var docs = sp.GetRequiredService<IOptionsMonitor<McpOptions>>().CurrentValue.Servers["docs"];

        Assert.Equal(ToolScope.Coding, docs.ParsedScope);
        Assert.Equal(["read*"], docs.Tools.Allow);
        Assert.False(docs.Tools.IsAllowed("write_file"));
    }

    [Fact]
    public async Task AddAgentMcp_KeepsPreRegisteredConnector()
    {
        var fake = new InProcessConnector();
        await using var sp = Build(Config(), s => s.AddSingleton<IMcpClientConnector>(fake));

        Assert.Same(fake, sp.GetRequiredService<IMcpClientConnector>());
    }

    [Fact]
    public async Task HostedProvider_StartsAndStopsWithNoServers()
    {
        await using var sp = Build(Config());
        var hosted = sp.GetRequiredService<IHostedService>();

        await hosted.StartAsync(TestContext.Current.CancellationToken);
        var status = await sp.GetRequiredService<IStatusContributor>().GetStatusAsync(TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal("no servers", status);
        Assert.Empty(sp.GetRequiredService<IToolSource>().GetTools());
    }
}
