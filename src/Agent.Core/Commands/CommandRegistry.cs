using Agent.Core.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Agent.Core.Commands;

public interface ICommandRegistry
{
    bool TryGet(string nameOrAlias, out CommandDescriptor command);

    IReadOnlyList<CommandDescriptor> All { get; }

    /// <summary>Validation problems found during the last build (duplicates, bad definitions).</summary>
    IReadOnlyList<string> Problems { get; }

    void Reload();
}

public sealed class CommandRegistry : ICommandRegistry, IReloadable
{
    private readonly IEnumerable<ICommandSource> _sources;
    private readonly ILogger<CommandRegistry> _logger;
    private readonly Lock _lock = new();
    private Dictionary<string, CommandDescriptor>? _byName;
    private List<CommandDescriptor> _all = [];
    private List<string> _problems = [];

    public CommandRegistry(IEnumerable<ICommandSource> sources, ILogger<CommandRegistry> logger)
    {
        _sources = sources;
        _logger = logger;
    }

    public string Name => "commands";

    public IReadOnlyList<CommandDescriptor> All
    {
        get
        {
            EnsureBuilt();
            return _all;
        }
    }

    public IReadOnlyList<string> Problems
    {
        get
        {
            EnsureBuilt();
            return _problems;
        }
    }

    public bool TryGet(string nameOrAlias, out CommandDescriptor command)
    {
        EnsureBuilt();
        return _byName!.TryGetValue(nameOrAlias.Trim().ToLowerInvariant(), out command!);
    }

    public void Reload()
    {
        lock (_lock)
        {
            _byName = null;
        }

        EnsureBuilt();
    }

    public Task ReloadAsync(CancellationToken cancellationToken)
    {
        Reload();
        return Task.CompletedTask;
    }

    private void EnsureBuilt()
    {
        if (_byName is not null)
        {
            return;
        }

        lock (_lock)
        {
            if (_byName is not null)
            {
                return;
            }

            var byName = new Dictionary<string, CommandDescriptor>(StringComparer.OrdinalIgnoreCase);
            var all = new List<CommandDescriptor>();
            var problems = new List<string>();

            foreach (var source in _sources)
            {
                IEnumerable<CommandDescriptor> commands;
                try
                {
                    commands = source.GetCommands().ToList();
                }
                catch (Exception ex)
                {
                    problems.Add($"{source.GetType().Name}: {ex.Message}");
                    _logger.LogError(ex, "Command source {Source} failed", source.GetType().Name);
                    continue;
                }

                problems.AddRange(source.Problems);

                foreach (var cmd in commands)
                {
                    var clash = new[] { cmd.Name }.Concat(cmd.Aliases).FirstOrDefault(byName.ContainsKey);
                    if (clash is not null)
                    {
                        problems.Add($"'{cmd.Name}' from {cmd.Source}: name or alias '{clash}' already used by '{byName[clash].Name}' ({byName[clash].Source}).");
                        continue;
                    }

                    byName[cmd.Name] = cmd;
                    foreach (var alias in cmd.Aliases)
                    {
                        byName[alias] = cmd;
                    }

                    all.Add(cmd);
                }
            }

            foreach (var problem in problems)
            {
                _logger.LogWarning("Command registry: {Problem}", problem);
            }

            _all = all.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
            _problems = problems;
            _byName = byName;
            _logger.LogInformation("Command registry built: {Count} commands, {Problems} problems", _all.Count, problems.Count);
        }
    }
}
