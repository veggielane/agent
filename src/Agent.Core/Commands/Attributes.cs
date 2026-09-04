using Agent.Core.Authorization;
using Agent.Core.Channels;

namespace Agent.Core.Commands;

/// <summary>Marks a method as a <c>!command</c>. The declaring type is resolved from DI when the command runs.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class CommandAttribute : Attribute
{
    public CommandAttribute(string name, string description)
    {
        Name = name;
        Description = description;
    }

    public string Name { get; }

    public string Description { get; }

    public string[] Aliases { get; set; } = [];

    /// <summary>Required. Minimum role that may run the command.</summary>
    public Role Role { get; set; }

    /// <summary>Null = every channel.</summary>
    public Channel[]? Channels { get; set; }

    public bool Hidden { get; set; }

    /// <summary>Also register the command as an LLM tool.</summary>
    public bool ExposeAsTool { get; set; }
}

/// <summary>A <c>--name value</c> / <c>--flag</c> parameter.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class OptionAttribute : Attribute
{
    public OptionAttribute(string name, string? description = null)
    {
        Name = name;
        Description = description;
    }

    public string Name { get; }

    public string? Description { get; }

    public char Short { get; set; }
}

/// <summary>Captures everything after the preceding positional parameters as one string, quotes preserved.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class RestAttribute : Attribute
{
}

/// <summary>Optional description for a positional parameter (shown in usage).</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ArgAttribute : Attribute
{
    public ArgAttribute(string description) => Description = description;

    public string Description { get; }
}
