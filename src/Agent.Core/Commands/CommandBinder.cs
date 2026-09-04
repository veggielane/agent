using System.ComponentModel;
using System.Globalization;

namespace Agent.Core.Commands;

/// <summary>Binds raw argument text to a command's parameters. Throws <see cref="CommandBindingException"/> with a usage hint.</summary>
public static class CommandBinder
{
    public static ParsedArgs Parse(CommandDescriptor command, string rawArgs)
    {
        var tokens = CommandTokenizer.Tokenize(rawArgs);
        var args = new ParsedArgs();
        var positionalParams = command.Parameters.Where(p => !p.IsOption).ToList();
        var restParam = positionalParams.FirstOrDefault(p => p.IsRest);
        var restIndex = restParam is null ? -1 : positionalParams.IndexOf(restParam);

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (token.IsLongOption || token.IsShortOption)
            {
                var (name, inlineValue) = SplitOption(token);
                var param = FindOption(command, name)
                    ?? throw new CommandBindingException($"Unknown option `--{name}`.");

                if (param.IsFlag)
                {
                    args.Options[param.Name] = inlineValue ?? "true";
                    continue;
                }

                if (inlineValue is not null)
                {
                    args.Options[param.Name] = inlineValue;
                    continue;
                }

                if (i + 1 >= tokens.Count || tokens[i + 1].IsLongOption)
                {
                    throw new CommandBindingException($"Option `--{param.Name}` needs a value.");
                }

                args.Options[param.Name] = tokens[++i].Value;
                continue;
            }

            if (restIndex >= 0 && args.Positionals.Count == restIndex)
            {
                args.Rest = rawArgs[token.Start..].Trim();
                args.Positionals.Add(args.Rest);
                break;
            }

            args.Positionals.Add(token.Value);
        }

        if (restIndex >= 0 && args.Positionals.Count == restIndex)
        {
            args.Rest = string.Empty;
        }

        return args;
    }

    /// <summary>Produces the argument list for a reflection invoke, in parameter order.</summary>
    public static object?[] Bind(CommandDescriptor command, ParsedArgs args, IReadOnlyList<BoundParameter> layout, CommandContext context)
    {
        var values = new object?[layout.Count];
        var positionalIndex = 0;

        for (var i = 0; i < layout.Count; i++)
        {
            var slot = layout[i];
            if (slot.IsContext)
            {
                values[i] = context;
                continue;
            }

            if (slot.IsCancellation)
            {
                values[i] = context.CancellationToken;
                continue;
            }

            var p = slot.Parameter!;
            string? raw;
            if (p.IsOption)
            {
                raw = args.Options.TryGetValue(p.Name, out var v) ? (v ?? "true") : null;
            }
            else if (p.IsRest)
            {
                raw = positionalIndex < args.Positionals.Count ? args.Rest : string.Empty;
                positionalIndex = args.Positionals.Count;
            }
            else
            {
                raw = positionalIndex < args.Positionals.Count ? args.Positionals[positionalIndex] : null;
                positionalIndex++;
            }

            if (raw is null)
            {
                if (p.Required)
                {
                    throw new CommandBindingException($"Missing {(p.IsOption ? "option --" : "argument <")}{p.Name}{(p.IsOption ? string.Empty : ">")}.");
                }

                values[i] = p.Default ?? DefaultOf(p.Type);
                continue;
            }

            values[i] = Convert(raw, p.Type, p.Name);
        }

        if (!command.Parameters.Any(p => p.IsRest) && positionalIndex < args.Positionals.Count)
        {
            throw new CommandBindingException($"Too many arguments (expected at most {command.Parameters.Count(p => !p.IsOption)}).");
        }

        return values;
    }

    public static object? Convert(string raw, Type type, string name)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        try
        {
            if (target == typeof(string))
            {
                return raw;
            }

            if (target == typeof(bool))
            {
                return raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1" || raw.Equals("yes", StringComparison.OrdinalIgnoreCase) || raw.Length == 0;
            }

            if (target.IsEnum)
            {
                return Enum.Parse(target, raw, ignoreCase: true);
            }

            if (target == typeof(int))
            {
                return int.Parse(raw.TrimStart('#'), CultureInfo.InvariantCulture);
            }

            if (target == typeof(long))
            {
                return long.Parse(raw.TrimStart('#'), CultureInfo.InvariantCulture);
            }

            if (target == typeof(Uri))
            {
                return new Uri(raw, UriKind.Absolute);
            }

            var converter = TypeDescriptor.GetConverter(target);
            if (converter.CanConvertFrom(typeof(string)))
            {
                return converter.ConvertFromInvariantString(raw);
            }

            throw new CommandBindingException($"Cannot convert '{raw}' for {name}.");
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or NotSupportedException or UriFormatException)
        {
            throw new CommandBindingException($"Invalid value '{raw}' for {name} (expected {Describe(target)}).");
        }
    }

    private static string Describe(Type t)
        => t.IsEnum ? string.Join("|", Enum.GetNames(t)) : t.Name.ToLowerInvariant();

    private static object? DefaultOf(Type t) => t.IsValueType && Nullable.GetUnderlyingType(t) is null ? Activator.CreateInstance(t) : null;

    private static (string Name, string? Value) SplitOption(CommandToken token)
    {
        var body = token.Value.TrimStart('-');
        var eq = body.IndexOf('=', StringComparison.Ordinal);
        return eq < 0 ? (body, null) : (body[..eq], body[(eq + 1)..]);
    }

    private static CommandParameter? FindOption(CommandDescriptor command, string name)
        => command.Parameters.FirstOrDefault(p => p.IsOption && (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) || (name.Length == 1 && p.Short == name[0])));
}

/// <summary>One method parameter slot: either a command parameter or an injected context value.</summary>
public sealed record BoundParameter(CommandParameter? Parameter, bool IsContext = false, bool IsCancellation = false);
