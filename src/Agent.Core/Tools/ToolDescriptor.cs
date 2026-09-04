using Agent.Core.Authorization;
using Agent.Core.Channels;
using Microsoft.Extensions.AI;

namespace Agent.Core.Tools;

[Flags]
public enum ToolScope
{
    None = 0,
    Answer = 1,
    Coding = 2,
    All = Answer | Coding,
}

/// <summary>An <see cref="AIFunction"/> plus the governance metadata the registry filters on.</summary>
public sealed class ToolDescriptor
{
    public ToolDescriptor(AIFunction function, Role role, ToolScope scope, string source, IReadOnlySet<Channel>? channels = null)
    {
        Function = function;
        Role = role;
        Scope = scope;
        Source = source;
        Channels = channels;
    }

    public AIFunction Function { get; }

    public string Name => Function.Name;

    /// <summary>Minimum role a caller needs before the tool is offered to the model.</summary>
    public Role Role { get; }

    public ToolScope Scope { get; }

    /// <summary>"native", "command", or the MCP server name.</summary>
    public string Source { get; }

    /// <summary>Null = every channel.</summary>
    public IReadOnlySet<Channel>? Channels { get; }

    public bool AppliesTo(CallerIdentity caller, ToolScope scope, Channel channel)
        => caller.HasRole(Role)
           && (Scope & scope) != 0
           && (Channels is null || Channels.Contains(channel));
}

/// <summary>Anything that contributes tools: native tool classes, the command registry, MCP servers.</summary>
public interface IToolSource
{
    IEnumerable<ToolDescriptor> GetTools();
}
