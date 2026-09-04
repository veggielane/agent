using Agent.Core.Authorization;
using Agent.Core.Channels;
using Microsoft.Extensions.Logging;

namespace Agent.Core.Tools;

public interface IToolRegistry
{
    /// <summary>Tools the caller may use in this scope and channel. Optional name filter (exact names).</summary>
    IReadOnlyList<ToolDescriptor> GetTools(CallerIdentity caller, ToolScope scope, Channel channel, IReadOnlyCollection<string>? filter = null);

    IReadOnlyList<ToolDescriptor> All();

    ToolDescriptor? Find(string name);
}

public sealed class ToolRegistry : IToolRegistry
{
    private readonly IEnumerable<IToolSource> _sources;
    private readonly ILogger<ToolRegistry> _logger;

    public ToolRegistry(IEnumerable<IToolSource> sources, ILogger<ToolRegistry> logger)
    {
        _sources = sources;
        _logger = logger;
    }

    public IReadOnlyList<ToolDescriptor> All()
    {
        var seen = new Dictionary<string, ToolDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in _sources)
        {
            IEnumerable<ToolDescriptor> tools;
            try
            {
                tools = source.GetTools().ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tool source {Source} failed; skipping", source.GetType().Name);
                continue;
            }

            foreach (var tool in tools)
            {
                if (seen.TryGetValue(tool.Name, out var existing))
                {
                    _logger.LogWarning("Duplicate tool name {Tool} from {Source} ignored (already provided by {Existing})", tool.Name, tool.Source, existing.Source);
                    continue;
                }

                seen[tool.Name] = tool;
            }
        }

        return seen.Values.ToList();
    }

    public IReadOnlyList<ToolDescriptor> GetTools(CallerIdentity caller, ToolScope scope, Channel channel, IReadOnlyCollection<string>? filter = null)
    {
        var filterSet = filter is null ? null : new HashSet<string>(filter, StringComparer.OrdinalIgnoreCase);
        return All()
            .Where(t => t.AppliesTo(caller, scope, channel))
            .Where(t => filterSet is null || filterSet.Contains(t.Name))
            .ToList();
    }

    public ToolDescriptor? Find(string name)
        => All().FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}
