using Agent.Core.Pipeline;
using Agent.Core.Tools;
using Microsoft.Extensions.AI;

namespace Agent.Core.Commands;

/// <summary>Exposes commands flagged <c>ExposeAsTool</c> as LLM functions named <c>cmd_&lt;name&gt;</c>.</summary>
public sealed class CommandToolSource : IToolSource
{
    private readonly ICommandRegistry _registry;
    private readonly IServiceProvider _services;

    public CommandToolSource(ICommandRegistry registry, IServiceProvider services)
    {
        _registry = registry;
        _services = services;
    }

    public IEnumerable<ToolDescriptor> GetTools()
    {
        foreach (var command in _registry.All.Where(c => c.ExposeAsTool))
        {
            var cmd = command;
            var function = AIFunctionFactory.Create(
                async (string arguments, CancellationToken ct) =>
                {
                    var scope = RequestContext.Current ?? throw new InvalidOperationException("No request context.");
                    var ctx = new CommandContext
                    {
                        Caller = scope.Caller,
                        Channel = scope.Caller.Channel,
                        ConversationId = scope.Event?.ConversationId ?? "tool",
                        Event = scope.Event,
                        Services = _services,
                        CancellationToken = ct,
                        RawArgs = arguments,
                        CommandName = cmd.Name,
                    };
                    var result = await cmd.Invoke(ctx, CommandBinder.Parse(cmd, arguments)).ConfigureAwait(false);
                    return result.Markdown;
                },
                name: "cmd_" + cmd.Name.Replace('-', '_'),
                description: $"{cmd.Description}. Arguments: {cmd.Usage(string.Empty)}");

            yield return new ToolDescriptor(function, cmd.Role, ToolScope.Answer, "command", cmd.Channels);
        }
    }
}
