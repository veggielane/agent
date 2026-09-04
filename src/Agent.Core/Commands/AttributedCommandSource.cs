using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Agent.Core.Commands;

/// <summary>DI marker: a type whose <c>[Command]</c> methods should be discovered.</summary>
public sealed record CommandHandlerRegistration(Type HandlerType);

/// <summary>Turns <c>[Command]</c>-attributed methods on registered handler types into descriptors.</summary>
public sealed class AttributedCommandSource : ICommandSource
{
    private readonly IEnumerable<CommandHandlerRegistration> _registrations;
    private readonly IServiceProvider _services;

    public AttributedCommandSource(IEnumerable<CommandHandlerRegistration> registrations, IServiceProvider services)
    {
        _registrations = registrations;
        _services = services;
    }

    private readonly List<string> _problems = [];

    public IReadOnlyList<string> Problems => _problems;

    public IEnumerable<CommandDescriptor> GetCommands()
    {
        _problems.Clear();
        var result = new List<CommandDescriptor>();
        foreach (var type in _registrations.Select(r => r.HandlerType).Distinct())
        {
            result.AddRange(FromType(type, _services, _problems));
        }

        return result;
    }

    public static IEnumerable<CommandDescriptor> FromType(Type type, IServiceProvider services, ICollection<string>? problems = null)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            var attr = method.GetCustomAttribute<CommandAttribute>();
            if (attr is null)
            {
                continue;
            }

            CommandDescriptor descriptor;
            try
            {
                descriptor = FromMethod(type, method, attr, services);
            }
            catch (CommandDefinitionException ex) when (problems is not null)
            {
                problems.Add($"{type.Name}.{method.Name}: {ex.Message}");
                continue;
            }

            yield return descriptor;
        }
    }

    public static CommandDescriptor FromMethod(Type type, MethodInfo method, CommandAttribute attr, IServiceProvider services)
    {
        var layout = new List<BoundParameter>();
        var parameters = new List<CommandParameter>();

        foreach (var p in method.GetParameters())
        {
            if (p.ParameterType == typeof(CommandContext))
            {
                layout.Add(new BoundParameter(null, IsContext: true));
                continue;
            }

            if (p.ParameterType == typeof(CancellationToken))
            {
                layout.Add(new BoundParameter(null, IsCancellation: true));
                continue;
            }

            var option = p.GetCustomAttribute<OptionAttribute>();
            var rest = p.GetCustomAttribute<RestAttribute>() is not null;
            var arg = p.GetCustomAttribute<ArgAttribute>();
            var hasDefault = p.HasDefaultValue;
            var nullable = !p.ParameterType.IsValueType || Nullable.GetUnderlyingType(p.ParameterType) is not null;
            var required = !hasDefault && !(option is not null && (p.ParameterType == typeof(bool))) && !(nullable && option is not null);

            if (rest)
            {
                // The remainder may legitimately be empty.
                required = false;
            }

            var cp = new CommandParameter(
                option?.Name ?? p.Name ?? $"arg{parameters.Count}",
                p.ParameterType,
                option is not null,
                rest,
                required,
                hasDefault ? p.DefaultValue : null,
                option?.Description ?? arg?.Description,
                option?.Short ?? '\0');

            parameters.Add(cp);
            layout.Add(new BoundParameter(cp));
        }

        var descriptorRef = new CommandDescriptor[1];
        CommandInvoker invoke = async (ctx, args) =>
        {
            var instance = method.IsStatic ? null : ActivatorUtilities.GetServiceOrCreateInstance(ctx.Services, type);
            var values = CommandBinder.Bind(descriptorRef[0], args, layout, ctx);
            object? result;
            try
            {
                result = method.Invoke(instance, values);
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                throw tie.InnerException;
            }

            return await ConvertResultAsync(result).ConfigureAwait(false);
        };

        var descriptor = new CommandDescriptor(
            attr.Name,
            attr.Description,
            attr.Role,
            invoke,
            parameters,
            attr.Aliases,
            attr.Channels is null ? null : new HashSet<Channels.Channel>(attr.Channels),
            attr.Hidden,
            attr.ExposeAsTool,
            source: type.Name);

        descriptorRef[0] = descriptor;
        return descriptor;
    }

    internal static async Task<CommandResult> ConvertResultAsync(object? result)
    {
        switch (result)
        {
            case null:
                return CommandResult.None;
            case CommandResult r:
                return r;
            case string s:
                return CommandResult.Text(s);
            case Task<CommandResult> t:
                return await t.ConfigureAwait(false);
            case Task<string> ts:
                return CommandResult.Text(await ts.ConfigureAwait(false));
            case ValueTask<CommandResult> vt:
                return await vt.ConfigureAwait(false);
            case Task task:
                await task.ConfigureAwait(false);
                return CommandResult.None;
            default:
                return CommandResult.Text(result.ToString() ?? string.Empty);
        }
    }
}

/// <summary>Adapts DI-registered <see cref="ICommand"/> instances.</summary>
public sealed class ClassCommandSource : ICommandSource
{
    private readonly IEnumerable<ICommand> _commands;

    private readonly List<string> _problems = [];

    public ClassCommandSource(IEnumerable<ICommand> commands) => _commands = commands;

    public IReadOnlyList<string> Problems => _problems;

    public IEnumerable<CommandDescriptor> GetCommands()
    {
        _problems.Clear();
        var result = new List<CommandDescriptor>();
        foreach (var command in _commands)
        {
            try
            {
                result.Add(command.Describe());
            }
            catch (CommandDefinitionException ex)
            {
                _problems.Add($"{command.GetType().Name}: {ex.Message}");
            }
        }

        return result;
    }
}
