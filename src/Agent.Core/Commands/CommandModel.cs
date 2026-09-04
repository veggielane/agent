using System.Text;
using Agent.Core.Authorization;
using Agent.Core.Channels;
using Agent.Core.Events;

namespace Agent.Core.Commands;

public sealed class CommandContext
{
    public required CallerIdentity Caller { get; init; }

    public required Channel Channel { get; init; }

    public required string ConversationId { get; init; }

    public InboundEvent? Event { get; init; }

    public required IServiceProvider Services { get; init; }

    public CancellationToken CancellationToken { get; init; }

    /// <summary>Everything after the command name, untouched.</summary>
    public string RawArgs { get; init; } = string.Empty;

    public string CommandName { get; init; } = string.Empty;
}

public sealed record CommandResult(string Markdown, bool IsError = false, bool Silent = false)
{
    public static CommandResult Text(string markdown) => new(markdown);

    public static CommandResult Error(string markdown) => new(markdown, IsError: true);

    public static CommandResult None { get; } = new(string.Empty, Silent: true);
}

public sealed record CommandParameter(
    string Name,
    Type Type,
    bool IsOption,
    bool IsRest,
    bool Required,
    object? Default,
    string? Description,
    char Short = '\0')
{
    public bool IsFlag => IsOption && (Type == typeof(bool) || Type == typeof(bool?));
}

/// <summary>Tokens after the command name, bound against a <see cref="CommandDescriptor"/>.</summary>
public sealed class ParsedArgs
{
    public List<string> Positionals { get; } = [];

    public Dictionary<string, string?> Options { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Rest { get; set; } = string.Empty;

    public string? Option(string name) => Options.TryGetValue(name, out var v) ? v : null;

    public bool Flag(string name) => Options.TryGetValue(name, out var v) && v is null or "true" or "1" or "";
}

public delegate Task<CommandResult> CommandInvoker(CommandContext context, ParsedArgs args);

public sealed class CommandDescriptor
{
    public CommandDescriptor(
        string name,
        string description,
        Role role,
        CommandInvoker invoke,
        IReadOnlyList<CommandParameter>? parameters = null,
        IReadOnlyList<string>? aliases = null,
        IReadOnlySet<Channel>? channels = null,
        bool hidden = false,
        bool exposeAsTool = false,
        string source = "code")
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Command name is required.", nameof(name));
        }

        if (role == 0)
        {
            throw new CommandDefinitionException($"Command '{name}' does not declare a role.");
        }

        Name = name.Trim().ToLowerInvariant();
        Description = description;
        Role = role;
        Invoke = invoke;
        Parameters = parameters ?? [];
        Aliases = (aliases ?? []).Select(a => a.Trim().ToLowerInvariant()).ToList();
        Channels = channels;
        Hidden = hidden;
        ExposeAsTool = exposeAsTool;
        Source = source;
    }

    public string Name { get; }

    public string Description { get; }

    public Role Role { get; }

    public CommandInvoker Invoke { get; }

    public IReadOnlyList<CommandParameter> Parameters { get; }

    public IReadOnlyList<string> Aliases { get; }

    public IReadOnlySet<Channel>? Channels { get; }

    public bool Hidden { get; }

    public bool ExposeAsTool { get; }

    public string Source { get; }

    public bool AppliesTo(Channel channel) => Channels is null || Channels.Contains(channel);

    public string Usage(string prefix = "!")
    {
        var sb = new StringBuilder().Append(prefix).Append(Name);
        foreach (var p in Parameters.Where(p => !p.IsOption))
        {
            sb.Append(' ').Append(p.Required ? $"<{p.Name}>" : $"[{p.Name}]");
            if (p.IsRest)
            {
                sb.Append("...");
            }
        }

        foreach (var p in Parameters.Where(p => p.IsOption))
        {
            sb.Append(' ').Append(p.IsFlag ? $"[--{p.Name}]" : $"[--{p.Name} <value>]");
        }

        return sb.ToString();
    }

    public string HelpText(string prefix = "!")
    {
        var sb = new StringBuilder();
        sb.Append('`').Append(Usage(prefix)).Append("` — ").Append(Description);
        if (Aliases.Count > 0)
        {
            sb.Append(" (aliases: ").Append(string.Join(", ", Aliases.Select(a => prefix + a))).Append(')');
        }

        sb.Append(" [").Append(Role).Append(']');
        var described = Parameters.Where(p => !string.IsNullOrWhiteSpace(p.Description)).ToList();
        foreach (var p in described)
        {
            sb.AppendLine().Append("  - ").Append(p.IsOption ? "--" : string.Empty).Append(p.Name).Append(": ").Append(p.Description);
        }

        return sb.ToString();
    }
}

/// <summary>A command implemented as a class (constructor injection, streaming, multi-step).</summary>
public interface ICommand
{
    CommandDescriptor Describe();
}

public interface ICommandSource
{
    IEnumerable<CommandDescriptor> GetCommands();

    /// <summary>Definition problems found during the last <see cref="GetCommands"/> call.</summary>
    IReadOnlyList<string> Problems { get; }
}

public sealed class CommandDefinitionException : Exception
{
    public CommandDefinitionException(string message)
        : base(message)
    {
    }
}

public sealed class CommandBindingException : Exception
{
    public CommandBindingException(string message)
        : base(message)
    {
    }
}
