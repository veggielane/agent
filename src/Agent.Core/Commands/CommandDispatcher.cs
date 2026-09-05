using System.Diagnostics;
using Agent.Core.Audit;
using Agent.Core.Authorization;
using Agent.Core.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agent.Core.Commands;

public interface ICommandDispatcher
{
    bool IsCommand(string? text);

    /// <summary>Runs the command in <paramref name="text"/>. Returns an error result for unknown commands when configured.</summary>
    Task<CommandResult> DispatchAsync(string text, CommandContext context);
}

public sealed class CommandDispatcher : ICommandDispatcher
{
    private readonly ICommandRegistry _registry;
    private readonly IAuthorizationService _authorization;
    private readonly IAuditSink _audit;
    private readonly IOptionsMonitor<CommandsOptions> _options;
    private readonly IOptionsMonitor<AuthorizationOptions> _authOptions;
    private readonly ILogger<CommandDispatcher> _logger;

    public CommandDispatcher(
        ICommandRegistry registry,
        IAuthorizationService authorization,
        IAuditSink audit,
        IOptionsMonitor<CommandsOptions> options,
        IOptionsMonitor<AuthorizationOptions> authOptions,
        ILogger<CommandDispatcher> logger)
    {
        _registry = registry;
        _authorization = authorization;
        _audit = audit;
        _options = options;
        _authOptions = authOptions;
        _logger = logger;
    }

    public bool IsCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var prefix = _options.CurrentValue.Prefix;
        var trimmed = text.TrimStart();
        return trimmed.StartsWith(prefix, StringComparison.Ordinal)
               && trimmed.Length > prefix.Length
               && char.IsLetter(trimmed[prefix.Length]);
    }

    public async Task<CommandResult> DispatchAsync(string text, CommandContext context)
    {
        var options = _options.CurrentValue;
        var body = text.TrimStart()[options.Prefix.Length..];
        var firstSpace = body.IndexOfAny([' ', '\t', '\n', '\r']);
        var name = firstSpace < 0 ? body : body[..firstSpace];
        var rawArgs = firstSpace < 0 ? string.Empty : body[(firstSpace + 1)..].Trim();

        if (!_registry.TryGet(name, out var command))
        {
            Record("unknown", name, Stopwatch.GetTimestamp());
            return options.ReplyToUnknown
                ? CommandResult.Error($"Unknown command `{options.Prefix}{name}`. Try `{options.Prefix}help`.")
                : CommandResult.None;
        }

        using var activity = AgentTelemetry.Source.StartActivity("agent.command", ActivityKind.Internal);
        activity
            .Tag("agent.command", command.Name)
            .Tag("agent.command.source", command.Source)
            .Tag("agent.command.role", command.Role.ToString())
            .Tag("agent.channel", context.Channel.ToString());

        var startedAt = Stopwatch.GetTimestamp();

        if (!command.AppliesTo(context.Channel))
        {
            Record("wrong-channel", command.Name, startedAt);
            return CommandResult.Error($"`{options.Prefix}{command.Name}` is not available on {context.Channel}.");
        }

        var auth = await _authorization.AuthorizeAsync(context.Caller, command.Role, $"command:{command.Name}", context.CancellationToken).ConfigureAwait(false);
        if (!auth.Allowed)
        {
            Record("denied", command.Name, startedAt);
            return context.Event is { IsPrivate: false } && _authOptions.CurrentValue.DenyBehaviour == DenyBehaviour.Silent
                ? CommandResult.None
                : CommandResult.Error(_authOptions.CurrentValue.DenyMessage);
        }

        var ctx = new CommandContext
        {
            Caller = auth.Caller,
            Channel = context.Channel,
            ConversationId = context.ConversationId,
            Event = context.Event,
            Services = context.Services,
            CancellationToken = context.CancellationToken,
            RawArgs = rawArgs,
            CommandName = command.Name,
        };

        try
        {
            var parsed = CommandBinder.Parse(command, rawArgs);
            var result = await command.Invoke(ctx, parsed).ConfigureAwait(false);
            await _audit.WriteAsync(new AuditEntry(DateTimeOffset.UtcNow, ctx.Channel, ctx.Caller.ChannelUserId, ctx.Caller.DisplayName, $"command:{command.Name}", result.IsError ? "error" : "ok", Shorten(rawArgs, 200), ctx.Caller.Roles), ctx.CancellationToken).ConfigureAwait(false);
            Record(result.IsError ? "error" : "ok", command.Name, startedAt);
            return result;
        }
        catch (CommandBindingException ex)
        {
            Record("binding-error", command.Name, startedAt);
            return CommandResult.Error($"{ex.Message}\nUsage: `{command.Usage(options.Prefix)}`");
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            Record("cancelled", command.Name, startedAt);
            throw;
        }
        catch (Exception ex)
        {
            var reference = Guid.NewGuid().ToString("N")[..6];
            activity.Failed(ex);
            activity.Tag("agent.error.reference", reference);
            Record("exception", command.Name, startedAt);
            _logger.LogError(ex, "Command {Command} failed (ref {Ref}) args='{Args}'", command.Name, reference, rawArgs);
            await _audit.WriteAsync(new AuditEntry(DateTimeOffset.UtcNow, ctx.Channel, ctx.Caller.ChannelUserId, ctx.Caller.DisplayName, $"command:{command.Name}", "exception", $"ref={reference} {ex.GetType().Name}", ctx.Caller.Roles), CancellationToken.None).ConfigureAwait(false);
            return CommandResult.Error($"`{options.Prefix}{command.Name}` failed (ref {reference}).");
        }

        void Record(string outcome, string commandName, long from)
        {
            var tags = new TagList
            {
                { "command", commandName },
                { "outcome", outcome },
                { "channel", context.Channel.ToString() },
            };
            AgentTelemetry.Commands.Add(1, tags);
            AgentTelemetry.CommandDuration.Record(Stopwatch.GetElapsedTime(from).TotalSeconds, tags);
            Activity.Current?.SetTag("agent.outcome", outcome);
        }
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
