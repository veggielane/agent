using Microsoft.Extensions.Hosting;

namespace Agent.Core.Commands.Yaml;

/// <summary>Starts the YAML directory watch and reloads the registry on change.</summary>
public sealed class CommandFileWatcher : IHostedService
{
    private readonly YamlCommandSource _source;
    private readonly ICommandRegistry _registry;

    public CommandFileWatcher(YamlCommandSource source, ICommandRegistry registry)
    {
        _source = source;
        _registry = registry;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _source.Watch(() => _registry.Reload());
        _registry.Reload();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
