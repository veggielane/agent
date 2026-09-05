using System.Diagnostics;
using System.Text.Json;
using Agent.Core.Audit;
using Agent.Core.Observability;
using Agent.Core.Pipeline;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agent.Core.Tools;

public sealed class ToolGovernance
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    public int MaxResultChars { get; init; } = 20_000;

    public int MaxArgumentChars { get; init; } = 20_000;
}

/// <summary>
/// Wraps any <see cref="AIFunction"/> with a timeout, result truncation, and an audit entry per call.
/// Used for MCP tools and optionally for native ones.
/// </summary>
public sealed class GovernedAIFunction : DelegatingAIFunction
{
    private readonly string _name;
    private readonly string _source;
    private readonly ToolGovernance _governance;
    private readonly IAuditSink _audit;
    private readonly ILogger _logger;

    public GovernedAIFunction(AIFunction inner, string name, string source, ToolGovernance governance, IAuditSink audit, ILogger logger)
        : base(inner)
    {
        _name = name;
        _source = source;
        _governance = governance;
        _audit = audit;
        _logger = logger;
    }

    public override string Name => _name;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var caller = RequestContext.Current?.Caller;
        var started = DateTimeOffset.UtcNow;
        var argsText = SafeSerialize(arguments);
        if (argsText.Length > _governance.MaxArgumentChars)
        {
            throw new InvalidOperationException($"Arguments for tool '{_name}' exceed {_governance.MaxArgumentChars} characters.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_governance.Timeout);

        using var activity = AgentTelemetry.Source.StartActivity("agent.tool", ActivityKind.Internal);
        activity
            .Tag("agent.tool", _name)
            .Tag("agent.tool.source", _source)
            .Tag("gen_ai.tool.name", _name);

        var stopwatch = Stopwatch.GetTimestamp();
        object? result;
        string outcome;
        try
        {
            result = await base.InvokeCoreAsync(arguments, timeout.Token).ConfigureAwait(false);
            outcome = "ok";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            outcome = "timeout";
            activity?.SetStatus(ActivityStatusCode.Error, outcome);
            RecordMetrics(outcome, stopwatch);
            await WriteAuditAsync(caller, outcome, started, argsText).ConfigureAwait(false);
            return $"Tool '{_name}' timed out after {_governance.Timeout.TotalSeconds:0}s.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            outcome = "error: " + ex.GetType().Name;
            activity.Failed(ex);
            RecordMetrics("error", stopwatch);
            await WriteAuditAsync(caller, outcome, started, argsText).ConfigureAwait(false);
            _logger.LogWarning(ex, "Tool {Tool} failed", _name);
            return $"Tool '{_name}' failed: {ex.Message}";
        }

        RecordMetrics(outcome, stopwatch);
        await WriteAuditAsync(caller, outcome, started, argsText).ConfigureAwait(false);
        return Truncate(result, _governance.MaxResultChars);
    }

    private void RecordMetrics(string outcome, long from)
    {
        var tags = new TagList { { "tool", _name }, { "source", _source }, { "outcome", outcome } };
        AgentTelemetry.Tools.Add(1, tags);
        AgentTelemetry.ToolDuration.Record(Stopwatch.GetElapsedTime(from).TotalSeconds, tags);
    }

    private async ValueTask WriteAuditAsync(Authorization.CallerIdentity? caller, string outcome, DateTimeOffset started, string args)
    {
        var elapsed = DateTimeOffset.UtcNow - started;
        await _audit.WriteAsync(new AuditEntry(
            started,
            caller?.Channel ?? Channels.Channel.Cli,
            caller?.ChannelUserId ?? "system",
            caller?.DisplayName,
            $"tool:{_name}",
            outcome,
            $"source={_source} ms={elapsed.TotalMilliseconds:0} args={Shorten(args, 200)}",
            caller?.Roles)).ConfigureAwait(false);
    }

    internal static object? Truncate(object? result, int max)
    {
        if (result is JsonElement { ValueKind: JsonValueKind.String } stringElement)
        {
            result = stringElement.GetString();
        }

        if (result is null)
        {
            return null;
        }

        var text = result as string ?? SafeSerialize(result);
        if (text.Length <= max)
        {
            return result is string ? result : text;
        }

        return text[..max] + $"\n…[truncated {text.Length - max} characters]";
    }

    private static string SafeSerialize(object value)
    {
        try
        {
            return value is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(value, AIJsonUtilities.DefaultOptions);
        }
        catch (Exception)
        {
            return value.ToString() ?? string.Empty;
        }
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
