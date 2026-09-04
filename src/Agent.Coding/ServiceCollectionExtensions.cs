using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agent.Coding;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="CodingOptions"/>, the process/git runners, the workspace manager and the coding engine.
    /// Requires <c>AddAgentCore</c> for the chat client factory, tool registry, and prompt provider.
    /// </summary>
    public static IServiceCollection AddAgentCoding(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(CodingOptions.SectionName);
        services.AddOptions<CodingOptions>()
            .Bind(section)
            .PostConfigure(options =>
            {
                // The binder appends configured array items to the defaults; a configured list should replace them.
                ReplaceIfConfigured(section, nameof(CodingOptions.AllowedExecutables), options.AllowedExecutables);
                ReplaceIfConfigured(section, nameof(CodingOptions.ProtectedPaths), options.ProtectedPaths);
                ReplaceIfConfigured(section, nameof(CodingOptions.AllowedGitSubcommands), options.AllowedGitSubcommands);
            });

        services.AddLogging();
        services.TryAddSingleton<IProcessRunner, CliWrapProcessRunner>();
        services.TryAddSingleton<IGitRunner, GitRunner>();
        services.TryAddSingleton<IWorkspaceManager, WorkspaceManager>();
        services.TryAddSingleton<ICodingEngine, CodingEngine>();
        return services;
    }

    private static void ReplaceIfConfigured(IConfiguration section, string key, List<string> target)
    {
        var configured = section.GetSection(key).GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToList();

        if (configured.Count == 0)
        {
            return;
        }

        target.Clear();
        target.AddRange(configured.Distinct(StringComparer.OrdinalIgnoreCase));
    }
}
