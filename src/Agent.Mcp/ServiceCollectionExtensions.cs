using Agent.Core;
using Agent.Core.Infrastructure;
using Agent.Core.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Agent.Mcp;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers MCP support: <see cref="McpOptions"/> from the <c>Mcp</c> section, the definition loader, the SDK
    /// connector (replaceable: register an <see cref="IMcpClientConnector"/> first), one <see cref="McpToolProvider"/>
    /// exposed as tool source, hosted service, reloadable and status contributor, and the <c>!mcp</c> command.
    /// Call after <c>AddAgentCore</c>.
    /// </summary>
    public static IServiceCollection AddAgentMcp(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<McpOptions>().Bind(configuration.GetSection(McpOptions.SectionName));

        services.TryAddSingleton<IMcpDefinitionLoader, McpDefinitionLoader>();
        services.TryAddSingleton<IMcpClientConnector, SdkMcpClientConnector>();

        services.TryAddSingleton<McpToolProvider>();
        services.TryAddSingleton<IMcpToolProvider>(sp => sp.GetRequiredService<McpToolProvider>());
        services.AddSingleton<IToolSource>(sp => sp.GetRequiredService<McpToolProvider>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<McpToolProvider>());
        services.AddSingleton<IReloadable>(sp => sp.GetRequiredService<McpToolProvider>());
        services.AddSingleton<IStatusContributor>(sp => sp.GetRequiredService<McpToolProvider>());

        services.AddCommandHandlers<McpCommands>();
        return services;
    }
}
