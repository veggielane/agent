using Agent.Core.Channels;

namespace Agent.Core.Formatting;

/// <summary>Turns the agent's canonical markdown into what a channel renders.</summary>
public interface IResponseFormatter
{
    Channel Channel { get; }

    string Format(string markdown);
}

public interface IFormatterRegistry
{
    string Format(Channel channel, string markdown);
}

public sealed class FormatterRegistry : IFormatterRegistry
{
    private readonly Dictionary<Channel, IResponseFormatter> _formatters;

    public FormatterRegistry(IEnumerable<IResponseFormatter> formatters)
    {
        _formatters = formatters.GroupBy(f => f.Channel).ToDictionary(g => g.Key, g => g.Last());
    }

    public string Format(Channel channel, string markdown)
        => _formatters.TryGetValue(channel, out var f) ? f.Format(markdown) : markdown;
}
